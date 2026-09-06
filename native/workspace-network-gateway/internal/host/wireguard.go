package host

import (
	"bufio"
	"bytes"
	"context"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"sort"
	"strconv"
	"strings"
	"time"

	"golang.zx2c4.com/wireguard/conn"
	"golang.zx2c4.com/wireguard/device"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

type wireGuardConfig struct {
	network protocol.NetworkConfiguration
	ipc     string
	peers   []device.NoisePublicKey
}

func RunWireGuard(ctx context.Context, options VPNRouteOptions) error {
	if ctx == nil {
		return errors.New("WireGuard context is required")
	}
	if err := options.validate(); err != nil {
		return err
	}
	content, err := readPrivateVPNConfig(options.ConfigPath)
	if err != nil {
		return errors.New("WireGuard configuration could not be read")
	}
	defer clearBytes(content)
	startup, stopStartup := context.WithTimeout(ctx, 35*time.Second)
	defer stopStartup()
	configuration, err := parseWireGuardConfig(startup, content)
	if err != nil {
		return err
	}
	tun := newMemoryTUN(int(configuration.network.MTU))
	vpn := device.NewDevice(tun, conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
	defer vpn.Close()
	if err := vpn.IpcSet(configuration.ipc); err != nil {
		return errors.New("WireGuard rejected the configuration")
	}
	configuration.ipc = ""
	if err := vpn.Up(); err != nil {
		return errors.New("WireGuard transport could not start")
	}
	err = waitWireGuardHandshake(startup, vpn, configuration.peers)
	stopStartup()
	if err != nil {
		return err
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	health := make(chan error, 1)
	healthDone := make(chan struct{})
	go func() { defer close(healthDone); health <- monitorWireGuard(ctx, vpn, configuration.peers) }()
	defer func() { cancel(); <-healthDone }()
	transport := memoryPacketTransport{tun}
	route := make(chan error, 1)
	// Selecting WireGuard is fail-closed for the entire workspace. AllowedIPs
	// limits the destinations its peers can carry, never authorizes host-direct
	// fallback (including for DNS destinations outside those prefixes).
	go func() { route <- runVPNRoute(ctx, options, transport, configuration.network, openConnectRoutePolicy{}) }()
	select {
	case err = <-route:
		cancel()
		<-health
		return err
	case err = <-health:
	case <-ctx.Done():
		err = ctx.Err()
	}
	cancel()
	_ = transport.Close()
	<-route
	return err
}

func initiateWireGuard(vpn *device.Device, peers []device.NoisePublicKey) {
	for _, key := range peers {
		if peer := vpn.LookupPeer(key); peer != nil {
			_ = peer.SendHandshakeInitiation(false)
		}
	}
}

func wireGuardHealthy(vpn *device.Device, peerCount int, now time.Time) bool {
	status := wireGuardHandshakeStatus{now: now}
	return vpn.IpcGetOperation(&status) == nil && status.healthy == peerCount
}

type wireGuardHandshakeStatus struct {
	now     time.Time
	healthy int
}

func (status *wireGuardHandshakeStatus) Write(snapshot []byte) (int, error) {
	// Core writes one complete UAPI snapshot. Inspect only handshake timestamps,
	// never retain the private key and preshared keys also present in that buffer.
	for _, line := range bytes.Split(snapshot, []byte{'\n'}) {
		if !bytes.HasPrefix(line, []byte("last_handshake_time_sec=")) {
			continue
		}
		seconds, err := strconv.ParseInt(string(line[len("last_handshake_time_sec="):]), 10, 64)
		age := status.now.Sub(time.Unix(seconds, 0))
		if err == nil && seconds > 0 && age >= 0 && age < 3*time.Minute {
			status.healthy++
		}
	}
	return len(snapshot), nil
}

func waitWireGuardHandshake(ctx context.Context, vpn *device.Device, peers []device.NoisePublicKey) error {
	initiateWireGuard(vpn, peers)
	ticker := time.NewTicker(100 * time.Millisecond)
	defer ticker.Stop()
	for {
		if wireGuardHealthy(vpn, len(peers), time.Now()) {
			return nil
		}
		select {
		case <-ctx.Done():
			return errors.New("WireGuard peer handshake did not complete")
		case <-ticker.C:
		}
	}
}

func monitorWireGuard(ctx context.Context, vpn *device.Device, peers []device.NoisePublicKey) error {
	ticker := time.NewTicker(30 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-ticker.C:
			if !wireGuardHealthy(vpn, len(peers), time.Now()) {
				return errors.New("WireGuard peer stopped responding")
			}
			initiateWireGuard(vpn, peers)
		}
	}
}

// Parse the common wg-quick file as data. Hooks and external includes are never
// executed. Diagnostics name fields only, since the payload includes private keys.
func parseWireGuardConfig(ctx context.Context, content []byte) (wireGuardConfig, error) {
	result := wireGuardConfig{network: protocol.NetworkConfiguration{InterfaceName: "gsnet0", MTU: 1420}}
	if len(content) > 1024*1024 {
		return result, errors.New("WireGuard configuration is too large")
	}
	ordered, err := orderWireGuardFields(content)
	if err != nil {
		return result, err
	}
	var ipc strings.Builder
	ipc.WriteString("replace_peers=true\n")
	section := ""
	private := false
	peerKey := false
	peerRoutes := false
	peerEndpoint := false
	finishPeer := func() bool { return section != "Peer" || peerKey && peerRoutes && peerEndpoint }
	scanner := bufio.NewScanner(strings.NewReader(ordered))
	for scanner.Scan() {
		line := strings.TrimSpace(strings.SplitN(scanner.Text(), "#", 2)[0])
		if line == "" || strings.HasPrefix(line, ";") {
			continue
		}
		if strings.HasPrefix(line, "[") {
			if !finishPeer() {
				return result, errors.New("WireGuard peer requires PublicKey, AllowedIPs and Endpoint")
			}
			if line != "[Interface]" && line != "[Peer]" {
				return result, errors.New("unsupported WireGuard configuration section")
			}
			section = strings.Trim(line, "[]")
			peerKey, peerRoutes, peerEndpoint = false, false, false
			continue
		}
		key, value, ok := strings.Cut(line, "=")
		key, value = strings.TrimSpace(key), strings.TrimSpace(value)
		if !ok {
			return result, errors.New("invalid WireGuard configuration field")
		}
		switch section + "." + key {
		case "Interface.PrivateKey", "Peer.PublicKey", "Peer.PresharedKey":
			decoded, err := base64.StdEncoding.DecodeString(value)
			if err != nil || len(decoded) != 32 {
				return result, errors.New("invalid WireGuard key")
			}
			name := "preshared_key"
			if key == "PrivateKey" {
				name = "private_key"
				private = true
			}
			if key == "PublicKey" {
				if peerKey {
					return result, errors.New("duplicate WireGuard peer key")
				}
				name = "public_key"
				peerKey = true
				var peer device.NoisePublicKey
				copy(peer[:], decoded)
				for _, existing := range result.peers {
					if existing == peer {
						clearBytes(decoded)
						return result, errors.New("duplicate WireGuard peer key")
					}
				}
				result.peers = append(result.peers, peer)
			}
			fmt.Fprintf(&ipc, "%s=%s\n", name, hex.EncodeToString(decoded))
			clearBytes(decoded)
		case "Interface.Address":
			for _, text := range strings.Split(value, ",") {
				prefix, err := netip.ParsePrefix(strings.TrimSpace(text))
				if err != nil || prefix.Bits() == 0 || !usableVPNAddress(prefix.Addr()) {
					return result, errors.New("invalid WireGuard interface address")
				}
				if prefix.Addr().Is4() {
					if result.network.IPv4Address != nil {
						return result, errors.New("WireGuard supports one interface address per family")
					}
					result.network.IPv4Address = &prefix
				} else {
					if result.network.IPv6Address != nil {
						return result, errors.New("WireGuard supports one interface address per family")
					}
					result.network.IPv6Address = &prefix
				}
			}
		case "Interface.DNS":
			for _, text := range strings.Split(value, ",") {
				address, err := netip.ParseAddr(strings.TrimSpace(text))
				if err != nil || !usableVPNAddress(address) {
					return result, errors.New("WireGuard DNS must contain IP addresses")
				}
				result.network.DNSServers = append(result.network.DNSServers, address)
			}
		case "Peer.AllowedIPs":
			if !peerKey {
				return result, errors.New("WireGuard PublicKey must precede peer routing fields")
			}
			for _, text := range strings.Split(value, ",") {
				prefix, err := netip.ParsePrefix(strings.TrimSpace(text))
				if err != nil {
					return result, errors.New("invalid WireGuard allowed prefix")
				}
				fmt.Fprintf(&ipc, "allowed_ip=%s\n", prefix.Masked())
				peerRoutes = true
			}
		case "Peer.Endpoint":
			if !peerKey {
				return result, errors.New("WireGuard PublicKey must precede endpoint")
			}
			host, port, err := net.SplitHostPort(value)
			if err != nil {
				return result, errors.New("invalid WireGuard endpoint")
			}
			p, err := strconv.ParseUint(port, 10, 16)
			if err != nil || p == 0 {
				return result, errors.New("invalid WireGuard endpoint port")
			}
			if _, err := netip.ParseAddr(host); err != nil {
				addresses, err := net.DefaultResolver.LookupNetIP(ctx, "ip", host)
				if err != nil || len(addresses) == 0 {
					return result, errors.New("WireGuard endpoint DNS failed")
				}
				host = addresses[0].String()
			}
			fmt.Fprintf(&ipc, "endpoint=%s\n", net.JoinHostPort(host, port))
			peerEndpoint = true
		case "Interface.MTU":
			mtu, err := strconv.ParseUint(value, 10, 16)
			if err != nil || mtu < 1280 {
				return result, errors.New("WireGuard MTU must be at least 1280")
			}
			result.network.MTU = uint16(mtu)
		case "Interface.ListenPort", "Peer.PersistentKeepalive":
			number, err := strconv.ParseUint(value, 10, 16)
			if err != nil {
				return result, errors.New("invalid WireGuard port or keepalive")
			}
			name := "listen_port"
			if key == "PersistentKeepalive" {
				name = "persistent_keepalive_interval"
			}
			fmt.Fprintf(&ipc, "%s=%d\n", name, number)
		default:
			return result, errors.New("unsupported WireGuard configuration field")
		}
	}
	if scanner.Err() != nil || !private || len(result.peers) == 0 || !finishPeer() || result.network.IPv4Address == nil && result.network.IPv6Address == nil {
		return result, errors.New("WireGuard requires an interface private key, address and complete peer")
	}
	ensureVPNDNS(&result.network)
	for _, address := range result.network.DNSServers {
		if address.Is4() && result.network.IPv4Address == nil || address.Is6() && result.network.IPv6Address == nil {
			return result, errors.New("WireGuard DNS requires a matching interface address family")
		}
	}
	if len(result.network.DNSServers) > 4 {
		return result, errors.New("WireGuard supports at most four DNS servers")
	}
	result.ipc = ipc.String()
	return result, nil
}

// wg-quick fields are unordered; UAPI fields are not. Canonicalize each section
// before translating it, keeping the interface before its peers and each public
// key before the peer settings it owns. Never execute wg-quick hook directives.
func orderWireGuardFields(content []byte) (string, error) {
	var sections [][]string
	scanner := bufio.NewScanner(strings.NewReader(string(content)))
	for scanner.Scan() {
		line := strings.TrimSpace(strings.SplitN(scanner.Text(), "#", 2)[0])
		if line == "" || strings.HasPrefix(line, ";") {
			continue
		}
		if strings.HasPrefix(line, "[") {
			if len(sections) == 0 && line != "[Interface]" || len(sections) > 0 && line != "[Peer]" {
				return "", errors.New("WireGuard requires one interface followed by peers")
			}
			sections = append(sections, []string{line})
		} else {
			if len(sections) == 0 {
				return "", errors.New("WireGuard field outside a section")
			}
			index := len(sections) - 1
			sections[index] = append(sections[index], line)
		}
	}
	if scanner.Err() != nil {
		return "", errors.New("invalid WireGuard configuration")
	}
	var result strings.Builder
	for _, section := range sections {
		seen := make(map[string]bool)
		fields := section[1:]
		for _, field := range fields {
			key, _, ok := strings.Cut(field, "=")
			key = strings.TrimSpace(key)
			if !ok || seen[key] {
				return "", errors.New("invalid or duplicate WireGuard field")
			}
			seen[key] = true
		}
		sort.SliceStable(fields, func(i, j int) bool {
			key, _, _ := strings.Cut(fields[i], "=")
			other, _, _ := strings.Cut(fields[j], "=")
			return strings.TrimSpace(key) == "PublicKey" && strings.TrimSpace(other) != "PublicKey"
		})
		result.WriteString(strings.Join(section, "\n"))
		result.WriteByte('\n')
	}
	return result.String(), nil
}
