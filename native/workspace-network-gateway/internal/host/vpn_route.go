package host

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"time"

	"golang.org/x/sys/unix"

	"github.com/terion-labs/asura/native/workspace-network-gateway/internal/protocol"
)

// VPNRouteOptions chooses exactly one projection of a host-owned packet engine.
// Isolated guests use authenticated packets; host clients use loopback TCP only.
type VPNRouteOptions struct {
	ConfigPath  string
	SocketPath  string
	KeyFilePath string
	SOCKSPort   uint16
	Ready       io.Writer
}

func readPrivateVPNConfig(path string) ([]byte, error) {
	fd, err := unix.Open(path, unix.O_RDONLY|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0)
	if err != nil {
		return nil, errors.New("VPN configuration could not be opened")
	}
	file := os.NewFile(uintptr(fd), "private-vpn-configuration")
	if file == nil {
		_ = unix.Close(fd)
		return nil, errors.New("invalid VPN configuration descriptor")
	}
	defer file.Close()
	var stat unix.Stat_t
	if err := unix.Fstat(fd, &stat); err != nil {
		return nil, errors.New("VPN configuration could not be inspected")
	}
	if stat.Mode&unix.S_IFMT != unix.S_IFREG || stat.Mode&0o077 != 0 || stat.Uid != uint32(os.Geteuid()) || stat.Size > 1024*1024 {
		return nil, errors.New("VPN configuration must be a bounded regular owner-private file")
	}
	content, err := io.ReadAll(io.LimitReader(file, 1024*1024+1))
	if err != nil || len(content) > 1024*1024 {
		clearBytes(content)
		return nil, errors.New("VPN configuration could not be read")
	}
	return content, nil
}

func (options VPNRouteOptions) validate() error {
	if options.Ready == nil || !filepath.IsAbs(options.ConfigPath) {
		return errors.New("VPN configuration and readiness writer are required")
	}
	if options.SOCKSPort != 0 {
		if options.SocketPath != "" || options.KeyFilePath != "" {
			return errors.New("choose a host socket or guest packet route, not both")
		}
		return nil
	}
	if !filepath.IsAbs(options.SocketPath) || !filepath.IsAbs(options.KeyFilePath) {
		return errors.New("guest socket and authentication key are required")
	}
	return nil
}

func runVPNRoute(ctx context.Context, options VPNRouteOptions, vpn io.ReadWriteCloser, config protocol.NetworkConfiguration, policy openConnectRoutePolicy) error {
	if options.SOCKSPort != 0 {
		return serveVPNSOCKS(ctx, options.SOCKSPort, vpn, config, policy, "", options.Ready)
	}
	key, err := readKeyFile(options.KeyFilePath)
	if err != nil {
		return err
	}
	defer clearBytes(key)
	dialer := net.Dialer{Timeout: connectTimeout}
	connection, err := dialer.DialContext(ctx, "unix", options.SocketPath)
	if err != nil {
		return err
	}
	defer connection.Close()
	stopCancellation := context.AfterFunc(ctx, func() { _ = connection.Close() })
	defer stopCancellation()
	if err := connection.SetDeadline(time.Now().Add(connectTimeout)); err != nil {
		return err
	}
	channel, err := protocol.ConnectHost(connection, key, config)
	if err != nil {
		return err
	}
	defer channel.Close()
	if err := connection.SetDeadline(time.Time{}); err != nil {
		return err
	}
	families := []string{}
	if config.IPv4Address != nil {
		families = append(families, "ipv4")
	}
	if config.IPv6Address != nil {
		families = append(families, "ipv6")
	}
	protocols := []string{"tcp", "udp", "control", "other"}
	if policy.usesDirectPath() {
		protocols = []string{"tcp", "udp"}
	}
	capabilities := Capabilities{Families: families, Protocols: protocols, MTU: config.MTU}
	return pumpVPNFD(ctx, vpn, channel, policy, int(config.MTU), func() error {
		_, err := fmt.Fprintln(options.Ready, capabilities.ReadinessLine())
		return err
	})
}

// A VPN without advertised DNS uses public resolvers inside the VPN, never the
// host resolver. Explicit provider DNS always takes precedence.
func ensureVPNDNS(config *protocol.NetworkConfiguration) {
	if len(config.DNSServers) != 0 {
		return
	}
	if config.IPv4Address != nil {
		config.DNSServers = []netip.Addr{netip.MustParseAddr("1.1.1.1"), netip.MustParseAddr("1.0.0.1")}
	} else {
		config.DNSServers = []netip.Addr{netip.MustParseAddr("2606:4700:4700::1111")}
	}
}
