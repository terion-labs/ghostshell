package host

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"net/netip"
	"sort"
	"testing"
	"time"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
	"golang.org/x/net/icmp"
	"golang.org/x/net/ipv4"
	"golang.org/x/net/ipv6"

	"github.com/terion-labs/asura/native/workspace-network-gateway/internal/protocol"
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
				Body: &icmp.Echo{ID: 0x4753, Seq: 17, Data: []byte("asura")},
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
			if echo.ID != 0x4753 || echo.Seq != 17 || !bytes.Equal(echo.Data, []byte("asura")) {
				t.Fatalf("echo payload changed: %#v", echo)
			}
		})
	}
}

func TestProxyPacketStreamDropsEchoInsteadOfFakingReply(t *testing.T) {
	t.Parallel()
	request := &icmp.Message{
		Type: ipv4.ICMPTypeEcho,
		Body: &icmp.Echo{ID: 0x4753, Seq: 17, Data: []byte("asura")},
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
	// An outbound route's source can be a VPN address without local self-delivery.
	// Select an owned local responder before starting the unchanged packet test.
	// A later packet-path failure must not trigger another candidate or a skip.
	deadline := time.Now().Add(4 * time.Second)
	interfaces, err := net.Interfaces()
	if err != nil {
		t.Fatalf("enumerate local UDP fixture interfaces: %v", err)
	}
	sort.Slice(interfaces, func(i, j int) bool { return interfaces[i].Name < interfaces[j].Name })
	seen := make(map[netip.Addr]bool)
	for _, iface := range interfaces {
		if iface.Flags&net.FlagUp == 0 || iface.Flags&(net.FlagLoopback|net.FlagPointToPoint) != 0 {
			continue
		}
		addresses, err := iface.Addrs()
		if err != nil {
			t.Logf("local UDP fixture interface %s: %v", iface.Name, err)
			continue
		}
		sort.Slice(addresses, func(i, j int) bool { return addresses[i].String() < addresses[j].String() })
		for _, value := range addresses {
			prefix, err := netip.ParsePrefix(value.String())
			if err != nil {
				continue
			}
			address := prefix.Addr().Unmap()
			if !address.Is4() || !address.IsGlobalUnicast() || address.IsLoopback() || address.IsLinkLocalUnicast() || seen[address] {
				continue
			}
			seen[address] = true
			if !time.Now().Before(deadline) {
				t.Fatal("local UDP fixture selection exhausted its four-second setup budget")
			}
			candidateDeadline := time.Now().Add(500 * time.Millisecond)
			if deadline.Before(candidateDeadline) {
				candidateDeadline = deadline
			}
			if err := checkLocalUDPSelfDelivery(address, candidateDeadline); err != nil {
				t.Logf("local UDP fixture candidate %s %s failed: %v", iface.Name, address, err)
				continue
			}
			t.Logf("local UDP fixture selected %s %s after owned self-delivery", iface.Name, address)
			return address
		}
	}
	t.Fatal("no existing UP non-loopback, non-point-to-point IPv4 address supports owned local UDP self-delivery")
	return netip.Addr{}
}

func checkLocalUDPSelfDelivery(address netip.Addr, deadline time.Time) error {
	server, err := net.ListenUDP("udp4", net.UDPAddrFromAddrPort(netip.AddrPortFrom(address, 0)))
	if err != nil {
		return fmt.Errorf("bind owned responder: %w", err)
	}
	defer server.Close()
	if err := server.SetDeadline(deadline); err != nil {
		return fmt.Errorf("set responder deadline: %w", err)
	}
	client, err := net.DialUDP("udp4", nil, server.LocalAddr().(*net.UDPAddr))
	if err != nil {
		return fmt.Errorf("connect to owned responder: %w", err)
	}
	defer client.Close()
	if err := client.SetDeadline(deadline); err != nil {
		return fmt.Errorf("set client deadline: %w", err)
	}
	want := make([]byte, 16)
	if _, err := rand.Read(want); err != nil {
		return fmt.Errorf("create fixture nonce: %w", err)
	}
	if _, err := client.Write(want); err != nil {
		return fmt.Errorf("send to owned responder: %w", err)
	}
	buffer := make([]byte, len(want)+1)
	count, peer, err := server.ReadFromUDP(buffer)
	if err != nil {
		return fmt.Errorf("owned responder receive: %w", err)
	}
	if peer.AddrPort() != client.LocalAddr().(*net.UDPAddr).AddrPort() || !bytes.Equal(buffer[:count], want) {
		return fmt.Errorf("owned responder received an unexpected peer or nonce")
	}
	if _, err := server.WriteToUDP(buffer[:count], peer); err != nil {
		return fmt.Errorf("owned responder reply: %w", err)
	}
	count, err = client.Read(buffer)
	if err != nil {
		return fmt.Errorf("receive owned responder reply: %w", err)
	}
	if !bytes.Equal(buffer[:count], want) {
		return fmt.Errorf("owned responder reply changed the nonce")
	}
	return nil
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
