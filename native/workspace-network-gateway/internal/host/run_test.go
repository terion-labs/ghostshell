package host

import (
	"bytes"
	"context"
	"encoding/binary"
	"io"
	"net"
	"net/netip"
	"testing"
	"time"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
	"golang.org/x/net/icmp"
	"golang.org/x/net/ipv4"
	"golang.org/x/net/ipv6"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

func TestDirectUpstreamCarriesTCP(t *testing.T) {
	t.Parallel()
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	want := []byte("workspace-gateway")
	serverFailure := make(chan error, 1)
	go func() {
		connection, acceptErr := listener.Accept()
		if acceptErr != nil {
			serverFailure <- acceptErr
			return
		}
		defer connection.Close()
		_, copyErr := io.Copy(connection, connection)
		serverFailure <- copyErr
	}()

	upstream, capabilities, err := newUpstream(Options{Mode: "direct", MTU: 1280})
	if err != nil {
		t.Fatal(err)
	}
	address := listener.Addr().(*net.TCPAddr).AddrPort()
	connection, err := upstream.DialContext(context.Background(), &M.Metadata{
		Network: M.TCP, DstIP: address.Addr(), DstPort: address.Port(),
	})
	if err != nil {
		t.Fatal(err)
	}
	if got := capabilities.ReadinessLine(); got != "READY v1 families=ipv4,ipv6 protocols=tcp,udp mtu=1280" {
		t.Fatalf("unexpected readiness line: %s", got)
	}
	_ = connection.SetDeadline(time.Now().Add(5 * time.Second))
	if _, err := connection.Write(want); err != nil {
		t.Fatal(err)
	}
	got := make([]byte, len(want))
	if _, err := io.ReadFull(connection, got); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(got, want) {
		t.Fatalf("TCP payload changed: %q", got)
	}
	_ = connection.Close()
	if err := <-serverFailure; err != nil {
		t.Fatal(err)
	}
}

func TestDirectUpstreamCarriesDNSOverUDP(t *testing.T) {
	t.Parallel()
	server, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer server.Close()
	serverFailure := make(chan error, 1)
	go func() {
		query := make([]byte, 512)
		count, peer, readErr := server.ReadFrom(query)
		if readErr != nil {
			serverFailure <- readErr
			return
		}
		binary.BigEndian.PutUint16(query[2:4], 0x8180)
		_, writeErr := server.WriteTo(query[:count], peer)
		serverFailure <- writeErr
	}()

	upstream, _, err := newUpstream(Options{Mode: "direct", MTU: 1280})
	if err != nil {
		t.Fatal(err)
	}
	packetConnection, err := upstream.DialUDP(nil)
	if err != nil {
		t.Fatal(err)
	}
	defer packetConnection.Close()
	_ = packetConnection.SetDeadline(time.Now().Add(5 * time.Second))
	query := make([]byte, 12)
	binary.BigEndian.PutUint16(query[:2], 0x4753)
	if _, err := packetConnection.WriteTo(query, server.LocalAddr()); err != nil {
		t.Fatal(err)
	}
	response := make([]byte, 512)
	count, _, err := packetConnection.ReadFrom(response)
	if err != nil {
		t.Fatal(err)
	}
	if count != len(query) || binary.BigEndian.Uint16(response[:2]) != 0x4753 || binary.BigEndian.Uint16(response[2:4]) != 0x8180 {
		t.Fatalf("unexpected DNS response: %x", response[:count])
	}
	if err := <-serverFailure; err != nil {
		t.Fatal(err)
	}
}

func TestNetworkConfigurationRequiresHostProvidedDNS(t *testing.T) {
	t.Parallel()
	if _, err := networkConfiguration(Options{MTU: 1280}); err == nil {
		t.Fatal("expected missing DNS configuration to fail")
	}
	if _, err := networkConfiguration(Options{
		MTU: 1280, DNSServers: []netip.Addr{netip.MustParseAddr("127.0.0.1")},
	}); err == nil {
		t.Fatal("expected guest-inaccessible loopback DNS to fail")
	}
	configuration, err := networkConfiguration(Options{
		MTU: 1280, DNSServers: []netip.Addr{netip.MustParseAddr("192.0.2.53")},
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(configuration.DNSServers) != 1 || configuration.DNSServers[0].String() != "192.0.2.53" {
		t.Fatalf("unexpected DNS configuration: %#v", configuration.DNSServers)
	}
}

func TestPacketChannelAndUserspaceStackCarryDNSRoundTrip(t *testing.T) {
	localAddress := routableLocalIPv4(t)
	server, err := net.ListenPacket("udp4", net.JoinHostPort(localAddress.String(), "0"))
	if err != nil {
		t.Fatal(err)
	}
	defer server.Close()
	serverFailure := make(chan error, 1)
	go func() {
		query := make([]byte, 512)
		count, peer, readErr := server.ReadFrom(query)
		if readErr != nil {
			serverFailure <- readErr
			return
		}
		binary.BigEndian.PutUint16(query[2:4], 0x8180)
		_, writeErr := server.WriteTo(query[:count], peer)
		serverFailure <- writeErr
	}()

	key := bytes.Repeat([]byte{3}, protocol.AuthenticationKeyLength)
	guestTransport, hostTransport := net.Pipe()
	deadline := time.Now().Add(5 * time.Second)
	_ = guestTransport.SetDeadline(deadline)
	_ = hostTransport.SetDeadline(deadline)
	configuration, err := networkConfiguration(Options{
		MTU: 1280, DNSServers: []netip.Addr{localAddress},
	})
	if err != nil {
		t.Fatal(err)
	}
	guestResult := make(chan *protocol.Channel, 1)
	guestFailure := make(chan error, 1)
	go func() {
		channel, connectErr := protocol.ConnectGuest(guestTransport, key, func(protocol.NetworkConfiguration) error { return nil })
		guestResult <- channel
		guestFailure <- connectErr
	}()
	hostChannel, err := protocol.ConnectHost(hostTransport, key, configuration)
	if err != nil {
		t.Fatal(err)
	}
	guestChannel := <-guestResult
	if err := <-guestFailure; err != nil {
		t.Fatal(err)
	}
	upstream, _, err := newUpstream(Options{Mode: "direct", MTU: 1280})
	if err != nil {
		t.Fatal(err)
	}
	plane, err := newDataPlane(&packetStream{channel: hostChannel, maximumPacketSize: 1280}, 1280, upstream)
	if err != nil {
		t.Fatal(err)
	}
	defer plane.Close()
	defer guestChannel.Close()

	query := make([]byte, 12)
	binary.BigEndian.PutUint16(query[:2], 0x4753)
	destination := server.LocalAddr().(*net.UDPAddr).AddrPort()
	packet := ipv4UDPPacket(guestIPv4.Addr(), 53000, destination, query)
	if err := guestChannel.SendPacket(packet); err != nil {
		t.Fatal(err)
	}
	response, err := guestChannel.ReceivePacket()
	if err != nil {
		t.Fatal(err)
	}
	if len(response) < 40 || response[9] != 17 {
		t.Fatalf("unexpected IP response: %x", response)
	}
	headerLength := int(response[0]&15) * 4
	payload := response[headerLength+8:]
	if binary.BigEndian.Uint16(payload[:2]) != 0x4753 || binary.BigEndian.Uint16(payload[2:4]) != 0x8180 {
		t.Fatalf("unexpected DNS payload: %x", payload)
	}
	if err := <-serverFailure; err != nil {
		t.Fatal(err)
	}
}

func TestDirectEchoForwarderUsesHostPingSockets(t *testing.T) {
	tests := []struct {
		name        string
		family      int
		source      netip.Addr
		destination netip.Addr
		requestType icmp.Type
		replyType   icmp.Type
	}{
		{"IPv4", 4, guestIPv4.Addr(), netip.MustParseAddr("127.0.0.1"), ipv4.ICMPTypeEcho, ipv4.ICMPTypeEchoReply},
		{"IPv6", 6, guestIPv6.Addr(), netip.MustParseAddr("::1"), ipv6.ICMPTypeEchoRequest, ipv6.ICMPTypeEchoReply},
	}
	for _, test := range tests {
		test := test
		t.Run(test.name, func(t *testing.T) {
			if !canOpenPingSocket(test.family) {
				t.Skip("unprivileged host ping socket is unavailable")
			}
			request := &icmp.Message{
				Type: test.requestType,
				Body: &icmp.Echo{ID: 0x4753, Seq: 17, Data: []byte("ghostshell")},
			}
			wire, err := request.Marshal(nil)
			if err != nil {
				t.Fatal(err)
			}
			packet := rawICMPEchoPacket(test.family, test.source, test.destination, wire)
			forwarder := newEchoForwarder(nil, 1280)
			response, err := forwarder.roundTrip(packet)
			forwarder.Close()
			if err != nil {
				t.Fatal(err)
			}
			parsed, ok := parseEchoReply(response, test.family)
			if !ok || parsed.Type != test.replyType {
				t.Fatalf("unexpected echo reply: %x", response)
			}
			echo := parsed.Body.(*icmp.Echo)
			if echo.ID != 0x4753 || echo.Seq != 17 || !bytes.Equal(echo.Data, []byte("ghostshell")) {
				t.Fatalf("echo payload changed: %#v", echo)
			}
		})
	}
}

func TestProxyPacketStreamDropsEchoInsteadOfFakingReply(t *testing.T) {
	t.Parallel()
	request := &icmp.Message{
		Type: ipv4.ICMPTypeEcho,
		Body: &icmp.Echo{ID: 0x4753, Seq: 17, Data: []byte("ghostshell")},
	}
	wire, err := request.Marshal(nil)
	if err != nil {
		t.Fatal(err)
	}
	packet := rawICMPEchoPacket(4, guestIPv4.Addr(), netip.MustParseAddr("192.0.2.1"), wire)
	if !(&packetStream{dropEcho: true}).interceptEcho(packet) {
		t.Fatal("proxy packet stream did not intercept the echo request")
	}
	if (&packetStream{}).interceptEcho(packet) {
		t.Fatal("packet stream intercepted echo without an explicit policy")
	}
}

func rawICMPEchoPacket(family int, source, destination netip.Addr, message []byte) []byte {
	if family == 4 {
		packet := make([]byte, 20+len(message))
		packet[0] = 0x45
		binary.BigEndian.PutUint16(packet[2:4], uint16(len(packet)))
		packet[8], packet[9] = 64, icmpProtocolV4
		copy(packet[12:16], source.AsSlice())
		copy(packet[16:20], destination.AsSlice())
		binary.BigEndian.PutUint16(packet[10:12], internetChecksum(packet[:20]))
		copy(packet[20:], message)
		return packet
	}
	packet := make([]byte, 40+len(message))
	packet[0] = 0x60
	binary.BigEndian.PutUint16(packet[4:6], uint16(len(message)))
	packet[6], packet[7] = icmpProtocolV6, 64
	copy(packet[8:24], source.AsSlice())
	copy(packet[24:40], destination.AsSlice())
	copy(packet[40:], message)
	return packet
}

func parseEchoReply(packet []byte, family int) (*icmp.Message, bool) {
	offset, protocolNumber := 20, icmpProtocolV4
	if family == 6 {
		offset, protocolNumber = 40, icmpProtocolV6
	}
	if len(packet) < offset+8 {
		return nil, false
	}
	message, err := icmp.ParseMessage(protocolNumber, packet[offset:])
	return message, err == nil
}

func routableLocalIPv4(t *testing.T) netip.Addr {
	t.Helper()
	probe, err := net.DialUDP("udp4", nil, &net.UDPAddr{IP: net.IPv4(192, 0, 2, 1), Port: 9})
	if err != nil {
		t.Skipf("host has no routable IPv4 interface: %v", err)
	}
	defer probe.Close()
	address, ok := netip.AddrFromSlice(probe.LocalAddr().(*net.UDPAddr).IP)
	if !ok || address.IsLoopback() || address.IsUnspecified() {
		t.Skip("host has no non-loopback IPv4 address")
	}
	return address.Unmap()
}

func ipv4UDPPacket(source netip.Addr, sourcePort uint16, destination netip.AddrPort, payload []byte) []byte {
	packet := make([]byte, 20+8+len(payload))
	packet[0] = 0x45
	binary.BigEndian.PutUint16(packet[2:4], uint16(len(packet)))
	packet[8] = 64
	packet[9] = 17
	copy(packet[12:16], source.AsSlice())
	copy(packet[16:20], destination.Addr().AsSlice())
	binary.BigEndian.PutUint16(packet[20:22], sourcePort)
	binary.BigEndian.PutUint16(packet[22:24], destination.Port())
	binary.BigEndian.PutUint16(packet[24:26], uint16(8+len(payload)))
	copy(packet[28:], payload)
	binary.BigEndian.PutUint16(packet[10:12], internetChecksum(packet[:20]))
	return packet
}

func internetChecksum(value []byte) uint16 {
	var sum uint32
	for index := 0; index < len(value); index += 2 {
		sum += uint32(binary.BigEndian.Uint16(value[index : index+2]))
	}
	for sum>>16 != 0 {
		sum = sum&0xffff + sum>>16
	}
	return ^uint16(sum)
}
