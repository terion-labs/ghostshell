package host

import (
	"bytes"
	"context"
	"errors"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"reflect"
	"testing"
	"time"

	"golang.org/x/sys/unix"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

func TestOpenConnectConfigurationUsesNegotiatedTunnelValues(t *testing.T) {
	t.Parallel()
	environment := environmentValues(map[string]string{
		"INTERNAL_IP4_MTU":        "1400",
		"INTERNAL_IP4_ADDRESS":    "10.20.30.40",
		"INTERNAL_IP4_NETMASKLEN": "24",
		"INTERNAL_IP6_ADDRESS":    "2001:db8::40",
		"INTERNAL_IP6_NETMASK":    "2001:db8::/64",
		"INTERNAL_IP4_DNS":        "10.20.30.1 2001:db8::53",
		"INTERNAL_IP6_DNS":        "2001:db8::53",
	})

	configuration, families, err := openConnectConfiguration(environment)
	if err != nil {
		t.Fatal(err)
	}
	if configuration.MTU != 1400 || configuration.IPv4Address.String() != "10.20.30.40/24" ||
		configuration.IPv6Address.String() != "2001:db8::40/64" {
		t.Fatalf("unexpected tunnel configuration: %#v", configuration)
	}
	if !reflect.DeepEqual(families, []string{"ipv4", "ipv6"}) {
		t.Fatalf("unexpected families: %v", families)
	}
	wantDNS := []netip.Addr{netip.MustParseAddr("10.20.30.1"), netip.MustParseAddr("2001:db8::53")}
	if !reflect.DeepEqual(configuration.DNSServers, wantDNS) {
		t.Fatalf("unexpected DNS servers: %v", configuration.DNSServers)
	}
}

func TestOpenConnectConfigurationRejectsUnsafeValues(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name   string
		values map[string]string
	}{
		{"missing MTU", map[string]string{"INTERNAL_IP4_ADDRESS": "10.0.0.2", "INTERNAL_IP4_NETMASKLEN": "24", "INTERNAL_IP4_DNS": "10.0.0.1"}},
		{"missing address", map[string]string{"INTERNAL_IP4_MTU": "1400", "INTERNAL_IP4_DNS": "10.0.0.1"}},
		{"missing DNS", map[string]string{"INTERNAL_IP4_MTU": "1400", "INTERNAL_IP4_ADDRESS": "10.0.0.2", "INTERNAL_IP4_NETMASKLEN": "24"}},
		{"loopback DNS", map[string]string{"INTERNAL_IP4_MTU": "1400", "INTERNAL_IP4_ADDRESS": "10.0.0.2", "INTERNAL_IP4_NETMASKLEN": "24", "INTERNAL_IP4_DNS": "127.0.0.1"}},
		{"cross-family DNS", map[string]string{"INTERNAL_IP4_MTU": "1400", "INTERNAL_IP4_ADDRESS": "10.0.0.2", "INTERNAL_IP4_NETMASKLEN": "24", "INTERNAL_IP4_DNS": "2001:db8::53"}},
		{"invalid netmask", map[string]string{"INTERNAL_IP4_MTU": "1400", "INTERNAL_IP4_ADDRESS": "10.0.0.2", "INTERNAL_IP4_NETMASK": "255.0.255.0", "INTERNAL_IP4_DNS": "10.0.0.1"}},
		{"small IPv4 MTU", map[string]string{"INTERNAL_IP4_MTU": "575", "INTERNAL_IP4_ADDRESS": "10.0.0.2", "INTERNAL_IP4_NETMASKLEN": "24", "INTERNAL_IP4_DNS": "10.0.0.1"}},
		{"small IPv6 MTU", map[string]string{"INTERNAL_IP4_MTU": "1200", "INTERNAL_IP6_ADDRESS": "2001:db8::2/64", "INTERNAL_IP6_DNS": "2001:db8::53"}},
	}
	for _, test := range tests {
		test := test
		t.Run(test.name, func(t *testing.T) {
			t.Parallel()
			if _, _, err := openConnectConfiguration(environmentValues(test.values)); err == nil {
				t.Fatal("expected invalid OpenConnect environment to fail")
			}
		})
	}
}

func TestOpenConnectConfigurationAllowsIPv4MinimumMTU(t *testing.T) {
	t.Parallel()
	configuration, families, err := openConnectConfiguration(environmentValues(map[string]string{
		"INTERNAL_IP4_MTU":        "576",
		"INTERNAL_IP4_ADDRESS":    "10.0.0.2",
		"INTERNAL_IP4_NETMASKLEN": "24",
		"INTERNAL_IP4_DNS":        "10.0.0.1",
	}))
	if err != nil {
		t.Fatal(err)
	}
	if configuration.MTU != 576 || !reflect.DeepEqual(families, []string{"ipv4"}) {
		t.Fatalf("unexpected IPv4-only configuration: %#v, %v", configuration, families)
	}
}

func TestOpenConnectSplitPolicyRoutesOnlyNegotiatedIncludesThroughVPN(t *testing.T) {
	t.Parallel()
	configuration := protocol.NetworkConfiguration{
		IPv4Address: prefixPointer(netip.MustParsePrefix("10.64.0.2/24")),
		DNSServers:  []netip.Addr{netip.MustParseAddr("10.20.30.53")},
	}
	policy, err := parseOpenConnectRoutePolicy(environmentValues(map[string]string{
		"CISCO_SPLIT_INC":            "2",
		"CISCO_SPLIT_INC_0_ADDR":     "10.0.0.0",
		"CISCO_SPLIT_INC_0_MASKLEN":  "8",
		"CISCO_SPLIT_INC_0_PROTOCOL": "0",
		"CISCO_SPLIT_INC_1_ADDR":     "192.168.40.0/24",
		"CISCO_SPLIT_INC_1_MASKLEN":  "24",
		"CISCO_SPLIT_EXC":            "1",
		"CISCO_SPLIT_EXC_0_ADDR":     "10.20.0.0",
		"CISCO_SPLIT_EXC_0_MASK":     "255.255.0.0",
	}), configuration)
	if err != nil {
		t.Fatal(err)
	}
	assertPacketRoute(t, policy, "10.1.2.3", true)
	assertPacketRoute(t, policy, "192.168.40.5", true)
	assertPacketRoute(t, policy, "10.20.40.5", false)
	assertPacketRoute(t, policy, "203.0.113.5", false)
	// Negotiated VPN DNS remains reachable even when a concentrator omits it
	// from its include list.
	assertPacketRoute(t, policy, "10.20.30.53", true)
	if !policy.usesDirectPath() {
		t.Fatal("split policy must start the host direct data plane")
	}
}

func TestOpenConnectFullTunnelRoutesEverythingThroughVPN(t *testing.T) {
	t.Parallel()
	policy, err := parseOpenConnectRoutePolicy(environmentValues(nil), protocol.NetworkConfiguration{
		IPv4Address: prefixPointer(netip.MustParsePrefix("10.64.0.2/24")),
		DNSServers:  []netip.Addr{netip.MustParseAddr("10.64.0.53")},
	})
	if err != nil {
		t.Fatal(err)
	}
	assertPacketRoute(t, policy, "1.1.1.1", true)
	if policy.usesDirectPath() {
		t.Fatal("full tunnel must not start a direct data plane")
	}
}

func TestOpenConnectZeroIncludesMeansDirectByDefault(t *testing.T) {
	t.Parallel()
	policy, err := parseOpenConnectRoutePolicy(environmentValues(map[string]string{
		"CISCO_SPLIT_INC": "0",
	}), protocol.NetworkConfiguration{
		IPv4Address: prefixPointer(netip.MustParsePrefix("10.64.0.2/24")),
		DNSServers:  []netip.Addr{netip.MustParseAddr("10.64.0.53")},
	})
	if err != nil {
		t.Fatal(err)
	}
	assertPacketRoute(t, policy, "1.1.1.1", false)
	assertPacketRoute(t, policy, "10.64.0.53", true)
}

func TestOpenConnectSplitPolicyRejectsIncompleteOrPortScopedRoutes(t *testing.T) {
	t.Parallel()
	configuration := protocol.NetworkConfiguration{
		IPv4Address: prefixPointer(netip.MustParsePrefix("10.64.0.2/24")),
		DNSServers:  []netip.Addr{netip.MustParseAddr("10.64.0.53")},
	}
	for _, values := range []map[string]string{
		{"CISCO_SPLIT_INC": "1", "CISCO_SPLIT_INC_0_ADDR": "10.0.0.0"},
		{"CISCO_SPLIT_INC": "1", "CISCO_SPLIT_INC_0_ADDR": "10.0.0.0", "CISCO_SPLIT_INC_0_MASKLEN": "8", "CISCO_SPLIT_INC_0_DPORT": "443"},
		{"CISCO_IPV6_SPLIT_INC": "1", "CISCO_IPV6_SPLIT_INC_0_ADDR": "2001:db8::", "CISCO_IPV6_SPLIT_INC_0_MASKLEN": "32"},
	} {
		if _, err := parseOpenConnectRoutePolicy(environmentValues(values), configuration); err == nil {
			t.Fatalf("expected invalid split policy to fail: %v", values)
		}
	}
}

func TestReadKeyFileRequiresOwnerOnlyRegularFile(t *testing.T) {
	t.Parallel()
	directory := t.TempDir()
	key := bytes.Repeat([]byte{0x47}, protocol.AuthenticationKeyLength)
	path := filepath.Join(directory, "key")
	if err := os.WriteFile(path, key, 0o600); err != nil {
		t.Fatal(err)
	}
	got, err := readKeyFile(path)
	if err != nil {
		t.Fatal(err)
	}
	defer clearBytes(got)
	if !bytes.Equal(got, key) {
		t.Fatal("key file contents changed")
	}
	if err := os.Chmod(path, 0o640); err != nil {
		t.Fatal(err)
	}
	if _, err := readKeyFile(path); err == nil {
		t.Fatal("expected group-readable key file to fail")
	}
	link := filepath.Join(directory, "key-link")
	if err := os.Symlink(path, link); err != nil {
		t.Fatal(err)
	}
	if _, err := readKeyFile(link); err == nil {
		t.Fatal("expected key file symlink to fail")
	}
}

func TestPumpVPNFDStopsAfterPeerAndGuestDisappear(t *testing.T) {
	guestChannel, hostChannel := connectedPacketChannels(t)
	descriptors, err := unix.Socketpair(unix.AF_UNIX, unix.SOCK_DGRAM, 0)
	if err != nil {
		t.Fatal(err)
	}
	vpn := os.NewFile(uintptr(descriptors[0]), "vpnfd")
	peer := os.NewFile(uintptr(descriptors[1]), "vpnfd-peer")
	result := make(chan error, 1)
	go func() {
		result <- pumpVPNFD(context.Background(), vpn, hostChannel, openConnectRoutePolicy{}, 1280, nil)
	}()
	packet := ipv4UDPPacket(netip.MustParseAddr("192.0.2.1"), 53, netip.MustParseAddrPort("100.64.0.2:53000"), []byte("probe"))
	if _, err := peer.Write(packet); err != nil {
		t.Fatal(err)
	}
	if _, err := guestChannel.ReceivePacket(); err != nil {
		t.Fatal(err)
	}
	// Let the VPN reader enter its next blocking read before its peer disappears.
	time.Sleep(50 * time.Millisecond)
	_ = peer.Close()
	guestChannel.Close()
	select {
	case err := <-result:
		if err == nil {
			t.Fatal("disconnected route unexpectedly succeeded")
		}
	case <-time.After(time.Second):
		t.Fatal("VPNFD pump remained blocked after losing both peers")
	}
}

func TestPumpVPNFDPreservesRawPacketDatagrams(t *testing.T) {
	guestChannel, hostChannel := connectedPacketChannels(t)
	defer guestChannel.Close()

	descriptors, err := unix.Socketpair(unix.AF_UNIX, unix.SOCK_DGRAM, 0)
	if err != nil {
		t.Fatal(err)
	}
	vpn := os.NewFile(uintptr(descriptors[0]), "vpnfd")
	peer := os.NewFile(uintptr(descriptors[1]), "vpnfd-peer")
	defer peer.Close()

	ctx, cancel := context.WithCancel(context.Background())
	result := make(chan error, 1)
	go func() { result <- pumpVPNFD(ctx, vpn, hostChannel, openConnectRoutePolicy{}, 1280, nil) }()

	inbound := ipv4UDPPacket(netip.MustParseAddr("192.0.2.1"), 53, netip.MustParseAddrPort("100.64.0.2:53000"), []byte("inbound"))
	if _, err := peer.Write(inbound); err != nil {
		t.Fatal(err)
	}
	if got, err := guestChannel.ReceivePacket(); err != nil || !bytes.Equal(got, inbound) {
		t.Fatalf("inbound VPNFD datagram changed: %x, %v", got, err)
	}

	outbound := ipv4UDPPacket(netip.MustParseAddr("100.64.0.2"), 53000, netip.MustParseAddrPort("192.0.2.1:53"), []byte("outbound"))
	if err := guestChannel.SendPacket(outbound); err != nil {
		t.Fatal(err)
	}
	received := make(chan []byte, 1)
	readFailure := make(chan error, 1)
	go func() {
		buffer := make([]byte, protocol.MaximumPayloadLength)
		count, readErr := peer.Read(buffer)
		if readErr != nil {
			readFailure <- readErr
			return
		}
		received <- buffer[:count]
	}()
	select {
	case got := <-received:
		if !bytes.Equal(got, outbound) {
			t.Fatalf("outbound VPNFD datagram changed: %x", got)
		}
	case err := <-readFailure:
		t.Fatal(err)
	case <-time.After(5 * time.Second):
		t.Fatal("timed out waiting for outbound VPNFD datagram")
	}

	cancel()
	select {
	case err := <-result:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("unexpected pump shutdown: %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("VPNFD pump did not stop after cancellation")
	}
}

func TestPumpVPNFDHandlesPacketBurstWhilePeerIsBusy(t *testing.T) {
	guestChannel, hostChannel := connectedPacketChannels(t)
	defer guestChannel.Close()
	descriptors, err := unix.Socketpair(unix.AF_UNIX, unix.SOCK_DGRAM, 0)
	if err != nil {
		t.Fatal(err)
	}
	vpn := os.NewFile(uintptr(descriptors[0]), "vpnfd")
	peer := os.NewFile(uintptr(descriptors[1]), "vpnfd-peer")
	defer interruptVPN(peer)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	result := make(chan error, 1)
	go func() { result <- pumpVPNFD(ctx, vpn, hostChannel, openConnectRoutePolicy{}, 1280, nil) }()
	const packets = 256
	received := make(chan error, 1)
	go func() {
		// Reproduce a browser burst while OpenConnect is occupied with TLS.
		time.Sleep(100 * time.Millisecond)
		buffer := make([]byte, protocol.MaximumPayloadLength)
		for range packets {
			if _, err := peer.Read(buffer); err != nil {
				received <- err
				return
			}
		}
		received <- nil
	}()
	packet := ipv4UDPPacket(guestIPv4.Addr(), 53000, netip.MustParseAddrPort("192.0.2.1:443"), make([]byte, 1200))
	for range packets {
		if err := guestChannel.SendPacket(packet); err != nil {
			t.Fatalf("burst stopped: %v; pump: %v", err, <-result)
		}
	}
	select {
	case err := <-result:
		t.Fatalf("pump stopped during burst: %v", err)
	case err := <-received:
		if err != nil {
			t.Fatal(err)
		}
	case <-ctx.Done():
		t.Fatal("burst did not drain")
	}
	cancel()
	if err := <-result; !errors.Is(err, context.Canceled) {
		t.Fatalf("unexpected shutdown: %v", err)
	}
}

func TestWriteVPNPacketRetriesOnlyTemporaryCongestion(t *testing.T) {
	for _, temporary := range []error{unix.ENOBUFS, unix.EAGAIN} {
		t.Run(temporary.Error(), func(t *testing.T) {
			calls := 0
			packet := []byte("same datagram")
			writer := vpnPacketWriter(func(value []byte) (int, error) {
				calls++
				if !bytes.Equal(value, packet) {
					t.Fatal("retry changed the datagram")
				}
				if calls == 1 {
					return 0, &os.PathError{Op: "write", Path: "vpnfd", Err: temporary}
				}
				return len(value), nil
			})
			count, err := writeVPNPacket(context.Background(), writer, packet)
			if err != nil || count != len(packet) || calls != 2 {
				t.Fatalf("retry: count=%d calls=%d err=%v", count, calls, err)
			}
		})
	}
	for _, failure := range []error{unix.ENOTCONN, unix.EPIPE, io.ErrClosedPipe} {
		calls := 0
		writer := vpnPacketWriter(func([]byte) (int, error) { calls++; return 0, failure })
		if _, err := writeVPNPacket(context.Background(), writer, []byte{1}); !errors.Is(err, failure) || calls != 1 {
			t.Fatalf("permanent failure retried or lost: calls=%d err=%v", calls, err)
		}
	}
}

func TestWriteVPNPacketCongestionIsCancellable(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	writer := vpnPacketWriter(func([]byte) (int, error) {
		cancel()
		return 0, unix.ENOBUFS
	})
	if _, err := writeVPNPacket(ctx, writer, []byte{1}); !errors.Is(err, context.Canceled) {
		t.Fatalf("congestion ignored cancellation: %v", err)
	}
}

type vpnPacketWriter func([]byte) (int, error)

func (writer vpnPacketWriter) Write(packet []byte) (int, error) { return writer(packet) }

func TestPumpVPNFDDispatchesSplitTrafficBetweenVPNAndHostDirectPath(t *testing.T) {
	localAddress := routableLocalIPv4(t)
	server, err := net.ListenPacket("udp4", net.JoinHostPort(localAddress.String(), "0"))
	if err != nil {
		t.Fatal(err)
	}
	defer server.Close()
	serverFailure := make(chan error, 1)
	go func() {
		buffer := make([]byte, 512)
		count, peerAddress, readErr := server.ReadFrom(buffer)
		if readErr == nil {
			_, readErr = server.WriteTo(buffer[:count], peerAddress)
		}
		serverFailure <- readErr
	}()

	guestChannel, hostChannel := connectedPacketChannels(t)
	defer guestChannel.Close()
	descriptors, err := unix.Socketpair(unix.AF_UNIX, unix.SOCK_DGRAM, 0)
	if err != nil {
		t.Fatal(err)
	}
	vpn := os.NewFile(uintptr(descriptors[0]), "vpnfd")
	peer := os.NewFile(uintptr(descriptors[1]), "vpnfd-peer")
	defer peer.Close()
	policy := openConnectRoutePolicy{
		ipv4: openConnectRouteFamily{
			includesConfigured: true,
			includes:           []netip.Prefix{netip.MustParsePrefix("192.0.2.0/24")},
		},
	}
	ctx, cancel := context.WithCancel(context.Background())
	result := make(chan error, 1)
	go func() { result <- pumpVPNFD(ctx, vpn, hostChannel, policy, 1280, nil) }()

	directPayload := []byte("host-direct")
	directPacket := ipv4UDPPacket(guestIPv4.Addr(), 53000, server.LocalAddr().(*net.UDPAddr).AddrPort(), directPayload)
	if err := guestChannel.SendPacket(directPacket); err != nil {
		t.Fatal(err)
	}
	directResponse, err := guestChannel.ReceivePacket()
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(directResponse[len(directResponse)-len(directPayload):], directPayload) {
		t.Fatalf("unexpected direct response: %x", directResponse)
	}
	if err := <-serverFailure; err != nil {
		t.Fatal(err)
	}

	vpnPacket := ipv4UDPPacket(guestIPv4.Addr(), 53001, netip.MustParseAddrPort("192.0.2.10:53"), []byte("vpn"))
	if err := guestChannel.SendPacket(vpnPacket); err != nil {
		t.Fatal(err)
	}
	vpnPacketResult := make(chan []byte, 1)
	vpnPacketFailure := make(chan error, 1)
	go func() {
		buffer := make([]byte, protocol.MaximumPayloadLength)
		count, readErr := peer.Read(buffer)
		if readErr != nil {
			vpnPacketFailure <- readErr
			return
		}
		vpnPacketResult <- append([]byte(nil), buffer[:count]...)
	}()
	select {
	case received := <-vpnPacketResult:
		if !bytes.Equal(received, vpnPacket) {
			t.Fatalf("VPN packet changed: %x", received)
		}
	case err := <-vpnPacketFailure:
		t.Fatal(err)
	case <-time.After(5 * time.Second):
		t.Fatal("timed out waiting for VPN packet")
	}

	cancel()
	select {
	case err := <-result:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("unexpected dispatcher shutdown: %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("split dispatcher did not stop after cancellation")
	}
}

func assertPacketRoute(t *testing.T, policy openConnectRoutePolicy, destination string, wantVPN bool) {
	t.Helper()
	packet := ipv4UDPPacket(guestIPv4.Addr(), 53000, netip.AddrPortFrom(netip.MustParseAddr(destination), 53), []byte("route"))
	got, err := policy.useVPN(packet)
	if err != nil {
		t.Fatal(err)
	}
	if got != wantVPN {
		t.Fatalf("route for %s: got VPN=%t, want %t", destination, got, wantVPN)
	}
}

func environmentValues(values map[string]string) func(string) string {
	return func(name string) string { return values[name] }
}

func connectedPacketChannels(t *testing.T) (*protocol.Channel, *protocol.Channel) {
	t.Helper()
	key := bytes.Repeat([]byte{0x53}, protocol.AuthenticationKeyLength)
	guestTransport, hostTransport := net.Pipe()
	deadline := time.Now().Add(5 * time.Second)
	_ = guestTransport.SetDeadline(deadline)
	_ = hostTransport.SetDeadline(deadline)
	configuration := protocol.NetworkConfiguration{
		InterfaceName: "gsnet0",
		MTU:           1280,
		IPv4Address:   prefixPointer(guestIPv4),
		DNSServers:    []netip.Addr{netip.MustParseAddr("192.0.2.53")},
	}
	guestResult := make(chan *protocol.Channel, 1)
	guestFailure := make(chan error, 1)
	go func() {
		channel, err := protocol.ConnectGuest(guestTransport, key, func(protocol.NetworkConfiguration) error { return nil })
		guestResult <- channel
		guestFailure <- err
	}()
	hostChannel, err := protocol.ConnectHost(hostTransport, key, configuration)
	if err != nil {
		t.Fatal(err)
	}
	guestChannel := <-guestResult
	if err := <-guestFailure; err != nil {
		t.Fatal(err)
	}
	return guestChannel, hostChannel
}
