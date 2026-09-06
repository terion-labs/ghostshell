package ethernet

import (
	"fmt"

	"gvisor.dev/gvisor/pkg/buffer"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/link/channel"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv6"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/icmp"
	"gvisor.dev/gvisor/pkg/tcpip/transport/udp"
)

const reassembledPacketMark = 1

// fragmentIngress uses gVisor's local receive path because the forwarding path
// performs conntrack before reassembly. It has no output route or host socket.
// Each direction owns separate bounded, expiring fragment queues in gVisor.
type fragmentIngress struct {
	stack   *stack.Stack
	link    *channel.Endpoint
	deliver func([]byte)
}

func newFragmentIngress(deliver func([]byte)) (*fragmentIngress, error) {
	s := stack.New(stack.Options{NetworkProtocols: []stack.NetworkProtocolFactory{ipv4.NewProtocol, ipv6.NewProtocol}, TransportProtocols: []stack.TransportProtocolFactory{newForwardedTCP, udp.NewProtocol, icmp.NewProtocol4}})
	f := &fragmentIngress{stack: s, link: channel.New(1, 65535, ""), deliver: deliver}
	if err := s.CreateNIC(1, f.link); err != nil {
		f.close()
		return nil, fmt.Errorf("create fragment ingress: %s", err)
	}
	if err := s.SetPromiscuousMode(1, true); err != nil {
		f.close()
		return nil, fmt.Errorf("configure fragment ingress: %s", err)
	}
	for _, number := range []tcpip.TransportProtocolNumber{header.TCPProtocolNumber, udp.ProtocolNumber} {
		s.SetTransportProtocolHandler(number, func(_ stack.TransportEndpointID, pkt *stack.PacketBuffer) bool { f.HandlePacket(pkt); return true })
	}
	if err := s.RegisterRawTransportEndpoint(ipv4.ProtocolNumber, icmp.ProtocolNumber4, f); err != nil {
		f.close()
		return nil, fmt.Errorf("configure fragment ICMP ingress: %s", err)
	}
	return f, nil
}

func (f *fragmentIngress) close() { f.stack.Close(); f.stack.Wait(); f.link.Close() }

// consume only diverts actual fragments. IPv6 extension chains before/after a
// fragment header are not flattened: those stay on the normal validation path
// and are not eligible for NAT reassembly here.
func (f *fragmentIngress) consume(packet []byte) bool {
	if len(packet) < 20 {
		return false
	}
	var number tcpip.NetworkProtocolNumber
	switch packet[0] >> 4 {
	case 4:
		h := header.IPv4(packet)
		if !h.More() && h.FragmentOffset() == 0 {
			return false
		}
		number = ipv4.ProtocolNumber
	case 6:
		if len(packet) < 48 || packet[6] != 44 {
			return false
		}
		// Only TCP/UDP are delivered after gVisor's IPv6 reassembly path.
		// Fragmented ICMPv6/NDP and chained extensions remain blocked.
		if packet[40] != byte(header.TCPProtocolNumber) && packet[40] != byte(udp.ProtocolNumber) {
			return true
		}
		number = ipv6.ProtocolNumber
	default:
		return false
	}
	pkt := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(append([]byte(nil), packet...))})
	f.link.InjectInbound(number, pkt)
	pkt.DecRef()
	return true
}

func (f *fragmentIngress) HandlePacket(pkt *stack.PacketBuffer) {
	network := pkt.NetworkHeader().Slice()
	if len(network) < 20 {
		return
	}
	if network[0]>>4 == 4 && (header.IPv4(network).More() || header.IPv4(network).FragmentOffset() != 0) {
		return
	}
	length := len(network)
	if network[0]>>4 == 6 {
		length = header.IPv6MinimumSize
	}
	packet := append([]byte(nil), network[:length]...)
	packet = append(packet, pkt.TransportHeader().Slice()...)
	packet = append(packet, pkt.Data().AsRange().ToSlice()...)
	if network[0]>>4 == 4 {
		h := header.IPv4(packet)
		h.SetChecksum(0)
		h.SetChecksum(^h.CalculateChecksum())
	} else {
		h := header.IPv6(packet)
		h.SetNextHeader(uint8(pkt.TransportProtocolNumber))
		h.SetPayloadLength(uint16(len(packet) - header.IPv6MinimumSize))
	}
	f.deliver(packet)
}

// restoreSourceFragments lets gVisor re-fragment only datagrams which arrived
// already fragmented and were assembled for NAT. Ordinary oversized IPv6
// packets still produce Packet Too Big; routers must not fragment those.
// NAT creates fresh fragment IDs using gVisor's collision-resistant allocator.
type restoreSourceFragments struct{}

func (*restoreSourceFragments) Action(pkt *stack.PacketBuffer, _ stack.Hook, _ *stack.Route, _ stack.AddressableEndpoint) (stack.RuleVerdict, int) {
	if pkt.Mark == reassembledPacketMark {
		pkt.NetworkPacketInfo.IsForwardedPacket = false
	}
	return stack.RuleAccept, 0
}
