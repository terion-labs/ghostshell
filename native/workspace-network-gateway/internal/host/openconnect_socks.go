package host

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

// RunOpenConnectSOCKS exposes the same server-advertised split policy as the
// guest packet route to host workspace clients. OpenConnect owns this process;
// loss of its packet channel closes the listener and every active connection.
func RunOpenConnectSOCKS(ctx context.Context, port uint16, environment func(string) string, ready io.Writer) error {
	if ctx == nil || environment == nil || ready == nil || port == 0 {
		return errors.New("context, loopback port, environment, and readiness output are required")
	}
	config, _, err := openConnectConfiguration(environment)
	if err != nil {
		return err
	}
	policy, err := parseOpenConnectRoutePolicy(environment, config)
	if err != nil {
		return err
	}
	vpn, err := openVPNFile(environment("VPNFD"))
	if err != nil {
		return err
	}
	defer vpn.Close()
	return serveVPNSOCKS(ctx, port, vpn, config, policy, environment("CISCO_DEF_DOMAIN"), ready)
}

// Each packet engine projects host sockets through its owned packet transport.
func serveVPNSOCKS(ctx context.Context, port uint16, vpn io.ReadWriteCloser, config protocol.NetworkConfiguration, policy openConnectRoutePolicy, domain string, ready io.Writer) error {
	s, err := newVPNSocketStack(ctx, vpn, config)
	if err != nil {
		return err
	}
	defer s.Close()
	listener, err := net.ListenTCP("tcp4", &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: int(port)})
	if err != nil {
		return err
	}
	defer listener.Close()
	var dnsSequence atomic.Uint64
	resolver := &net.Resolver{PreferGo: true, Dial: func(ctx context.Context, network, _ string) (net.Conn, error) {
		server := config.DNSServers[(dnsSequence.Add(1)-1)%uint64(len(config.DNSServers))]
		return s.dialDNS(ctx, network, server)
	}}
	lookupNetwork := "ip"
	if config.IPv6Address == nil {
		lookupNetwork = "ip4"
	}
	if config.IPv4Address == nil {
		lookupNetwork = "ip6"
	}
	dial := func(ctx context.Context, host string, port uint16) (net.Conn, error) {
		address, err := netip.ParseAddr(host)
		addresses := []netip.Addr{address}
		if err != nil {
			// Use only VPN DNS and its default domain, not the host resolver's
			// search domains. The trailing dot suppresses host search expansion.
			if !strings.Contains(host, ".") && domain != "" {
				host += "." + domain
			}
			var lookupErr error
			addresses, lookupErr = resolver.LookupNetIP(ctx, lookupNetwork, strings.TrimSuffix(host, ".")+".")
			if lookupErr != nil {
				return nil, lookupErr
			}
			if len(addresses) == 0 {
				return nil, errors.New("VPN DNS returned no address")
			}
		}
		return dialVPNAddresses(ctx, addresses, port, func(ctx context.Context, destination netip.AddrPort) (net.Conn, error) {
			if policy.useVPNAddress(destination.Addr()) {
				return s.dialTCP(ctx, destination)
			}
			// Only an excluded split destination uses the direct path. A failed
			// VPN connection never retries that address outside the VPN.
			direct := net.Dialer{Timeout: connectTimeout}
			return direct.DialContext(ctx, "tcp", destination.String())
		})
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	stopListener := context.AfterFunc(ctx, func() { _ = listener.Close() })
	defer stopListener()
	served := make(chan error, 1)
	go func() { served <- serveOpenConnectSOCKS(ctx, listener, dial) }()
	if _, err := fmt.Fprintln(ready, "READY v1 socks5 split-routes"); err != nil {
		cancel()
		<-served
		return err
	}
	select {
	case err = <-s.done:
		err = fmt.Errorf("OpenConnect packet channel stopped: %w", err)
	case err = <-served:
		return err
	case <-ctx.Done():
		err = ctx.Err()
	}
	cancel()
	<-served
	return err
}

func dialVPNAddresses(ctx context.Context, addresses []netip.Addr, port uint16, dial func(context.Context, netip.AddrPort) (net.Conn, error)) (net.Conn, error) {
	var failures []error
	for index, address := range addresses {
		budget := connectTimeout / time.Duration(len(addresses)-index)
		if deadline, ok := ctx.Deadline(); ok {
			budget = time.Until(deadline) / time.Duration(len(addresses)-index)
		}
		attempt, cancel := context.WithTimeout(ctx, budget)
		connection, err := dial(attempt, netip.AddrPortFrom(address.Unmap(), port))
		cancel()
		if err == nil {
			return connection, nil
		}
		failures = append(failures, err)
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
	}
	if len(failures) == 0 {
		return nil, errors.New("VPN DNS returned no address")
	}
	return nil, errors.Join(failures...)
}

func serveOpenConnectSOCKS(ctx context.Context, listener net.Listener, dial func(context.Context, string, uint16) (net.Conn, error)) error {
	ctx, cancel := context.WithCancel(ctx)
	var clients sync.WaitGroup
	defer clients.Wait()
	defer cancel()
	// Bound outstanding SOCKS requests, including clients stalled in negotiation.
	slots := make(chan struct{}, 256)
	for {
		client, err := listener.Accept()
		if err != nil {
			return err
		}
		select {
		case slots <- struct{}{}:
		default:
			_ = client.Close()
			continue
		}
		clients.Add(1)
		go func() {
			defer clients.Done()
			defer func() { <-slots }()
			defer client.Close()
			stop := context.AfterFunc(ctx, func() { _ = client.Close() })
			defer stop()
			_ = client.SetDeadline(time.Now().Add(connectTimeout))
			host, port, err := readSOCKSDestination(client)
			if err != nil {
				return
			}
			connectCtx, cancel := context.WithTimeout(ctx, connectTimeout)
			upstream, err := dial(connectCtx, host, port)
			cancel()
			if err != nil {
				_, _ = client.Write([]byte{5, 1, 0, 1, 0, 0, 0, 0, 0, 0})
				return
			}
			defer upstream.Close()
			stopUpstream := context.AfterFunc(ctx, func() { _ = upstream.Close() })
			defer stopUpstream()
			if _, err := client.Write([]byte{5, 0, 0, 1, 0, 0, 0, 0, 0, 0}); err != nil {
				return
			}
			_ = client.SetDeadline(time.Time{})
			done := make(chan struct{})
			go func() { _, _ = io.Copy(upstream, client); _ = upstream.Close(); close(done) }()
			_, _ = io.Copy(client, upstream)
			_ = client.Close()
			_ = upstream.Close()
			<-done
		}()
	}
}

func readSOCKSDestination(stream io.ReadWriter) (string, uint16, error) {
	var greeting [2]byte
	if _, err := io.ReadFull(stream, greeting[:]); err != nil {
		return "", 0, err
	}
	if greeting[0] != 5 || greeting[1] == 0 {
		return "", 0, errors.New("invalid SOCKS greeting")
	}
	methods := make([]byte, int(greeting[1]))
	if _, err := io.ReadFull(stream, methods); err != nil {
		return "", 0, err
	}
	accepted := false
	for _, method := range methods {
		accepted = accepted || method == 0
	}
	if !accepted {
		_, _ = stream.Write([]byte{5, 255})
		return "", 0, errors.New("SOCKS authentication method unavailable")
	}
	if _, err := stream.Write([]byte{5, 0}); err != nil {
		return "", 0, err
	}
	var request [4]byte
	if _, err := io.ReadFull(stream, request[:]); err != nil {
		return "", 0, err
	}
	if request[0] != 5 || request[1] != 1 || request[2] != 0 {
		return "", 0, errors.New("only SOCKS5 CONNECT is supported")
	}
	length := 0
	switch request[3] {
	case 1:
		length = 4
	case 4:
		length = 16
	case 3:
		var size [1]byte
		if _, err := io.ReadFull(stream, size[:]); err != nil {
			return "", 0, err
		}
		length = int(size[0])
	}
	if length == 0 {
		return "", 0, errors.New("invalid SOCKS destination")
	}
	address := make([]byte, length+2)
	if _, err := io.ReadFull(stream, address); err != nil {
		return "", 0, err
	}
	port := binary.BigEndian.Uint16(address[length:])
	if port == 0 {
		return "", 0, errors.New("invalid SOCKS port")
	}
	if request[3] == 3 {
		return string(address[:length]), port, nil
	}
	ip, ok := netip.AddrFromSlice(address[:length])
	if !ok {
		return "", 0, errors.New("invalid SOCKS address")
	}
	return ip.String(), port, nil
}
