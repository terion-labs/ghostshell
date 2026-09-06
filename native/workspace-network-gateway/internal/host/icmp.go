package host

import (
	"bytes"
	"encoding/binary"
	"errors"
	"net"
	"net/netip"
	"sync"
	"time"

	"golang.org/x/net/icmp"
	"golang.org/x/net/ipv4"
	"golang.org/x/net/ipv6"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

const (
	icmpProtocolV4 = 1
	icmpProtocolV6 = 58
	icmpTimeout    = 5 * time.Second
	maximumPings   = 32
)

var (
	errNotEchoRequest      = errors.New("packet is not an ICMP echo request")
	errUnexpectedEchoReply = errors.New("received an unexpected ICMP echo reply")
)

type echoForwarder struct {
	channel      *protocol.Channel
	maximumSize  int
	supportsIPv4 bool
	supportsIPv6 bool
	semaphore    chan struct{}
	mutex        sync.Mutex
	active       map[*icmp.PacketConn]struct{}
	wait         sync.WaitGroup
	closed       bool
}

func newEchoForwarder(channel *protocol.Channel, maximumSize int) *echoForwarder {
	return &echoForwarder{
		channel:      channel,
		maximumSize:  maximumSize,
		supportsIPv4: canOpenPingSocket(4),
		supportsIPv6: canOpenPingSocket(6),
		semaphore:    make(chan struct{}, maximumPings),
		active:       make(map[*icmp.PacketConn]struct{}),
	}
}

func (forwarder *echoForwarder) SupportsIPv4() bool { return forwarder.supportsIPv4 }

func (forwarder *echoForwarder) SupportsIPv6() bool { return forwarder.supportsIPv6 }

func (forwarder *echoForwarder) Forward(packet []byte) bool {
	request, ok := parseEchoRequest(packet)
	if !ok {
		return false
	}
	if request.family == 4 && !forwarder.supportsIPv4 || request.family == 6 && !forwarder.supportsIPv6 {
		return true
	}

	forwarder.mutex.Lock()
	if forwarder.closed {
		forwarder.mutex.Unlock()
		return true
	}
	select {
	case forwarder.semaphore <- struct{}{}:
		forwarder.wait.Add(1)
	default:
		forwarder.mutex.Unlock()
		return true
	}
	forwarder.mutex.Unlock()
	packetCopy := append([]byte(nil), packet...)
	go func() {
		defer func() {
			clearBytes(packetCopy)
			<-forwarder.semaphore
			forwarder.wait.Done()
		}()
		if response, err := forwarder.roundTrip(packetCopy); err == nil && len(response) <= forwarder.maximumSize {
			_ = forwarder.channel.SendPacket(response)
			clearBytes(response)
		}
	}()
	return true
}

func (forwarder *echoForwarder) Close() {
	forwarder.mutex.Lock()
	if forwarder.closed {
		forwarder.mutex.Unlock()
		return
	}
	forwarder.closed = true
	for connection := range forwarder.active {
		_ = connection.Close()
	}
	forwarder.mutex.Unlock()
	forwarder.wait.Wait()
}

func (forwarder *echoForwarder) roundTrip(packet []byte) ([]byte, error) {
	request, ok := parseEchoRequest(packet)
	if !ok {
		return nil, errNotEchoRequest
	}
	connection, err := openPingSocket(request.family)
	if err != nil {
		return nil, err
	}
	if !forwarder.track(connection) {
		return nil, net.ErrClosed
	}
	defer func() {
		forwarder.untrack(connection)
		_ = connection.Close()
	}()
	_ = connection.SetDeadline(time.Now().Add(icmpTimeout))
	wireRequest, err := request.message.Marshal(nil)
	if err != nil {
		return nil, err
	}
	destination := &net.UDPAddr{IP: net.IP(request.destination.AsSlice())}
	if _, err := connection.WriteTo(wireRequest, destination); err != nil {
		return nil, err
	}

	buffer := make([]byte, forwarder.maximumSize)
	for {
		count, peer, err := connection.ReadFrom(buffer)
		if err != nil {
			return nil, err
		}
		response, parseErr := icmp.ParseMessage(request.protocol, buffer[:count])
		if peerAddress(peer) != request.destination || parseErr != nil || !isMatchingEchoReply(request, response) {
			continue
		}
		requestEcho := request.message.Body.(*icmp.Echo)
		response.Body.(*icmp.Echo).ID = requestEcho.ID
		return marshalEchoReply(request, response)
	}
}

func (forwarder *echoForwarder) track(connection *icmp.PacketConn) bool {
	forwarder.mutex.Lock()
	defer forwarder.mutex.Unlock()
	if forwarder.closed {
		_ = connection.Close()
		return false
	}
	forwarder.active[connection] = struct{}{}
	return true
}

func (forwarder *echoForwarder) untrack(connection *icmp.PacketConn) {
	forwarder.mutex.Lock()
	delete(forwarder.active, connection)
	forwarder.mutex.Unlock()
}

type echoRequest struct {
	family      int
	protocol    int
	source      netip.Addr
	destination netip.Addr
	message     *icmp.Message
}

func parseEchoRequest(packet []byte) (echoRequest, bool) {
	if len(packet) < 20 {
		return echoRequest{}, false
	}
	switch packet[0] >> 4 {
	case 4:
		headerLength := int(packet[0]&15) * 4
		totalLength := int(binary.BigEndian.Uint16(packet[2:4]))
		fragment := binary.BigEndian.Uint16(packet[6:8])
		if headerLength < 20 || headerLength > len(packet) || totalLength != len(packet) ||
			fragment&0x3fff != 0 || packet[9] != icmpProtocolV4 {
			return echoRequest{}, false
		}
		message, err := icmp.ParseMessage(icmpProtocolV4, packet[headerLength:])
		source, sourceOK := netip.AddrFromSlice(packet[12:16])
		destination, destinationOK := netip.AddrFromSlice(packet[16:20])
		if err != nil || !sourceOK || !destinationOK || message.Type != ipv4.ICMPTypeEcho || message.Code != 0 {
			return echoRequest{}, false
		}
		if _, ok := message.Body.(*icmp.Echo); !ok {
			return echoRequest{}, false
		}
		return echoRequest{family: 4, protocol: icmpProtocolV4, source: source, destination: destination, message: message}, true
	case 6:
		if len(packet) < 40 || int(binary.BigEndian.Uint16(packet[4:6])) != len(packet)-40 || packet[6] != icmpProtocolV6 {
			return echoRequest{}, false
		}
		message, err := icmp.ParseMessage(icmpProtocolV6, packet[40:])
		source, sourceOK := netip.AddrFromSlice(packet[8:24])
		destination, destinationOK := netip.AddrFromSlice(packet[24:40])
		if err != nil || !sourceOK || !destinationOK || message.Type != ipv6.ICMPTypeEchoRequest || message.Code != 0 {
			return echoRequest{}, false
		}
		if _, ok := message.Body.(*icmp.Echo); !ok {
			return echoRequest{}, false
		}
		return echoRequest{family: 6, protocol: icmpProtocolV6, source: source, destination: destination, message: message}, true
	default:
		return echoRequest{}, false
	}
}

func isMatchingEchoReply(request echoRequest, response *icmp.Message) bool {
	if response == nil || response.Code != 0 {
		return false
	}
	wantType := icmp.Type(ipv4.ICMPTypeEchoReply)
	if request.family == 6 {
		wantType = ipv6.ICMPTypeEchoReply
	}
	responseEcho, ok := response.Body.(*icmp.Echo)
	requestEcho := request.message.Body.(*icmp.Echo)
	return ok && response.Type == wantType && responseEcho.Seq == requestEcho.Seq && bytes.Equal(responseEcho.Data, requestEcho.Data)
}

func marshalEchoReply(request echoRequest, response *icmp.Message) ([]byte, error) {
	var pseudoHeader []byte
	if request.family == 6 {
		pseudoHeader = icmp.IPv6PseudoHeader(net.IP(request.destination.AsSlice()), net.IP(request.source.AsSlice()))
	}
	message, err := response.Marshal(pseudoHeader)
	if err != nil {
		return nil, err
	}
	if request.family == 4 {
		packet := make([]byte, 20+len(message))
		packet[0] = 0x45
		binary.BigEndian.PutUint16(packet[2:4], uint16(len(packet)))
		packet[8], packet[9] = 64, icmpProtocolV4
		copy(packet[12:16], request.destination.AsSlice())
		copy(packet[16:20], request.source.AsSlice())
		binary.BigEndian.PutUint16(packet[10:12], checksum(packet[:20]))
		copy(packet[20:], message)
		return packet, nil
	}
	packet := make([]byte, 40+len(message))
	packet[0] = 0x60
	binary.BigEndian.PutUint16(packet[4:6], uint16(len(message)))
	packet[6], packet[7] = icmpProtocolV6, 64
	copy(packet[8:24], request.destination.AsSlice())
	copy(packet[24:40], request.source.AsSlice())
	copy(packet[40:], message)
	return packet, nil
}

func canOpenPingSocket(family int) bool {
	connection, err := openPingSocket(family)
	if err != nil {
		return false
	}
	_ = connection.Close()
	return true
}

func openPingSocket(family int) (*icmp.PacketConn, error) {
	if family == 4 {
		return icmp.ListenPacket("udp4", "0.0.0.0")
	}
	return icmp.ListenPacket("udp6", "::")
}

func peerAddress(peer net.Addr) netip.Addr {
	switch value := peer.(type) {
	case *net.IPAddr:
		address, _ := netip.AddrFromSlice(value.IP)
		return address.Unmap()
	case *net.UDPAddr:
		return value.AddrPort().Addr().Unmap()
	default:
		return netip.Addr{}
	}
}

func checksum(value []byte) uint16 {
	var sum uint32
	for len(value) >= 2 {
		sum += uint32(binary.BigEndian.Uint16(value[:2]))
		value = value[2:]
	}
	if len(value) == 1 {
		sum += uint32(value[0]) << 8
	}
	for sum>>16 != 0 {
		sum = sum&0xffff + sum>>16
	}
	return ^uint16(sum)
}
