// Package ethernet owns the host-side virtual gateway. Its only uplink is the
// authenticated provider packet channel; it never opens host Internet sockets.
package ethernet

import (
	"fmt"
	"net/netip"

	"github.com/terion-labs/asura/native/workspace-network-gateway/internal/protocol"
	"gvisor.dev/gvisor/pkg/buffer"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/link/channel"
	ethernetlink "gvisor.dev/gvisor/pkg/tcpip/link/ethernet"
	"gvisor.dev/gvisor/pkg/tcpip/network/arp"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv6"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/icmp"
	"gvisor.dev/gvisor/pkg/tcpip/transport/udp"
)

const (
	MTU                     = 1280
	GatewayMAC              = tcpip.LinkAddress("\x02\x47\x53\x4e\x57\x01")
	guestNIC    tcpip.NICID = 1
	providerNIC tcpip.NICID = 2
)

var (
	guest4   = netip.MustParseAddr("100.64.0.2")
	guest6   = netip.MustParseAddr("fd00:4753:4e57::2")
	gateway4 = netip.MustParseAddr("100.64.0.1")
	gateway6 = netip.MustParseAddr("fd00:4753:4e57::1")
)

// A router is one route generation, including its neighbour and NAT state.
// Nothing in it survives provider replacement.
type router struct {
	stack             *stack.Stack
	guest             *channel.Endpoint
	provider          *channel.Endpoint
	guestFragments    *fragmentIngress
	providerFragments *fragmentIngress
}

func newRouter(config protocol.NetworkConfiguration) (*router, error) {
	s := stack.New(stack.Options{
		NetworkProtocols:   []stack.NetworkProtocolFactory{arp.NewProtocol, ipv4.NewProtocol, ipv6.NewProtocol},
		TransportProtocols: []stack.TransportProtocolFactory{newForwardedTCP, udp.NewProtocol, icmp.NewProtocol4, icmp.NewProtocol6},
	})
	r := &router{stack: s, guest: channel.New(256, MTU+header.EthernetMinimumSize, GatewayMAC), provider: channel.New(256, uint32(config.MTU), "")}
	if err := s.CreateNICWithOptions(guestNIC, ethernetlink.New(r.guest), stack.NICOptions{Name: "workspace"}); err != nil {
		r.close()
		return nil, fmt.Errorf("create workspace NIC: %s", err)
	}
	if err := s.CreateNICWithOptions(providerNIC, r.provider, stack.NICOptions{Name: "provider"}); err != nil {
		r.close()
		return nil, fmt.Errorf("create provider NIC: %s", err)
	}
	var routes []tcpip.Route
	for _, family := range []struct {
		guest, gateway netip.Addr
		prefix         *netip.Prefix
	}{
		{guest4, gateway4, config.IPv4Address}, {guest6, gateway6, config.IPv6Address},
	} {
		// The guest NIC retains both gateway addresses even when one family has
		// no uplink. No default route is installed for that unsupported family.
		if err := addAddress(s, guestNIC, netip.PrefixFrom(family.gateway, family.gateway.BitLen())); err != nil {
			r.close()
			return nil, err
		}
		addr := tcpip.AddrFromSlice(family.guest.AsSlice())
		routes = append(routes, tcpip.Route{Destination: tcpip.AddressWithPrefix{Address: addr, PrefixLen: family.guest.BitLen()}.Subnet(), NIC: guestNIC})
		if family.prefix == nil {
			continue
		}
		// Proxy providers use the stable guest address too. Assigning that
		// address locally would consume returning packets instead of routing
		// them to the guest. Real VPN addresses remain local so ICMP errors
		// (including IPv6 Packet Too Big) have a valid provider-side source.
		if family.prefix.Addr() != family.guest && family.prefix.Addr() != family.gateway {
			if err := addAddress(s, providerNIC, *family.prefix); err != nil {
				r.close()
				return nil, err
			}
		}
		number := networkProtocol(family.guest)
		if err := s.SetForwardingDefaultAndAllNICs(number, true); err != nil {
			r.close()
			return nil, fmt.Errorf("enable forwarding: %s", err)
		}
		empty := header.IPv4EmptySubnet
		if family.guest.Is6() {
			empty = header.IPv6EmptySubnet
		}
		routes = append(routes, tcpip.Route{Destination: empty, NIC: providerNIC})
		installNAT(s, family.guest, family.gateway, family.prefix.Addr(), config.DNSServers)
	}
	s.SetRouteTable(routes)
	var err error
	r.guestFragments, err = newFragmentIngress(func(packet []byte) {
		data := make([]byte, header.EthernetMinimumSize+len(packet))
		header.Ethernet(data).Encode(&header.EthernetFields{SrcAddr: "\x02\x47\x53\x4e\x57\x02", DstAddr: GatewayMAC, Type: tcpip.NetworkProtocolNumber(header.IPv4ProtocolNumber)})
		if packet[0]>>4 == 6 {
			header.Ethernet(data).Encode(&header.EthernetFields{SrcAddr: "\x02\x47\x53\x4e\x57\x02", DstAddr: GatewayMAC, Type: header.IPv6ProtocolNumber})
		}
		copy(data[header.EthernetMinimumSize:], packet)
		injectMarked(r.guest, 0, data, reassembledPacketMark)
	})
	if err != nil {
		r.close()
		return nil, err
	}
	r.providerFragments, err = newFragmentIngress(func(packet []byte) { r.injectProvider(packet, reassembledPacketMark) })
	if err != nil {
		r.close()
		return nil, err
	}
	return r, nil
}

func addAddress(s *stack.Stack, nic tcpip.NICID, prefix netip.Prefix) error {
	err := s.AddProtocolAddress(nic, tcpip.ProtocolAddress{Protocol: networkProtocol(prefix.Addr()), AddressWithPrefix: tcpip.AddressWithPrefix{Address: tcpip.AddrFromSlice(prefix.Addr().AsSlice()), PrefixLen: prefix.Bits()}}, stack.AddressProperties{})
	if err != nil {
		return fmt.Errorf("assign gateway address: %s", err)
	}
	return nil
}

func networkProtocol(address netip.Addr) tcpip.NetworkProtocolNumber {
	if address.Is4() {
		return ipv4.ProtocolNumber
	}
	return ipv6.ProtocolNumber
}

func (r *router) close() {
	if r.guestFragments != nil {
		r.guestFragments.close()
	}
	if r.providerFragments != nil {
		r.providerFragments.close()
	}
	r.stack.Close()
	r.stack.Wait()
	r.guest.Close()
	r.provider.Close()
}

// injectFrame limits a workspace to its assigned addresses, while leaving ARP
// and IPv6 neighbour discovery to gVisor. No VLAN or alternative L2 protocol is
// accepted, and malformed frames cannot create a forwarding path.
func (r *router) injectFrame(frame []byte) {
	if len(frame) < header.EthernetMinimumSize || len(frame) > MTU+header.EthernetMinimumSize {
		return
	}
	eth := header.Ethernet(frame)
	if eth.DestinationAddress() != GatewayMAC && eth.DestinationAddress() != header.EthernetBroadcastAddress && !header.IsMulticastEthernetAddress(eth.DestinationAddress()) {
		return
	}
	ip := frame[header.EthernetMinimumSize:]
	switch eth.Type() {
	case header.ARPProtocolNumber:
		if len(ip) < header.ARPSize {
			return
		}
	case header.IPv4ProtocolNumber:
		if len(ip) < header.IPv4MinimumSize || header.IPv4(ip).SourceAddress() != tcpip.AddrFromSlice(guest4.AsSlice()) {
			return
		}
	case header.IPv6ProtocolNumber:
		if len(ip) < header.IPv6MinimumSize {
			return
		}
		h := header.IPv6(ip)
		// Link-local/unspecified sources are necessary for normal NDP/DAD,
		// but must never be allowed onto the provider NIC.
		if h.SourceAddress() != tcpip.AddrFromSlice(guest6.AsSlice()) && !header.IsV6MulticastAddress(h.DestinationAddress()) {
			return
		}
	default:
		return
	}
	if eth.Type() != header.ARPProtocolNumber && r.guestFragments.consume(ip) {
		return
	}
	injectMarked(r.guest, 0, append([]byte(nil), frame...), 0)
}

func (r *router) injectPacket(packet []byte) {
	if len(packet) == 0 {
		return
	}
	if r.providerFragments.consume(packet) {
		return
	}
	r.injectProvider(packet, 0)
}

func (r *router) injectProvider(packet []byte, mark uint32) {
	number := ipv4.ProtocolNumber
	if packet[0]>>4 == 6 {
		number = ipv6.ProtocolNumber
	}
	injectMarked(r.provider, number, packet, mark)
}

func injectMarked(link *channel.Endpoint, number tcpip.NetworkProtocolNumber, packet []byte, mark uint32) {
	pkt := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(packet), Mark: mark})
	link.InjectInbound(number, pkt)
	pkt.DecRef()
}
