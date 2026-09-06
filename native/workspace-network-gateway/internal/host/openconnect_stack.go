package host

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"sync"

	"gvisor.dev/gvisor/pkg/buffer"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/adapters/gonet"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/link/channel"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv6"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/tcp"
	"gvisor.dev/gvisor/pkg/tcpip/transport/udp"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

// vpnSocketStack terminates host TCP/UDP sockets on OpenConnect's raw packet
// channel. It owns no host interface, route, or DNS setting.
type vpnSocketStack struct {
	stack     *stack.Stack
	link      *channel.Endpoint
	vpn       io.ReadWriteCloser
	cancel    context.CancelFunc
	done      chan error
	workers   sync.WaitGroup
	closeOnce sync.Once
}

func newVPNSocketStack(ctx context.Context, vpn io.ReadWriteCloser, config protocol.NetworkConfiguration) (*vpnSocketStack, error) {
	// Inherited VPNFD is a blocking Unix datagram descriptor. Register a
	// duplicate with Go's network poller so Close reliably interrupts Read,
	// including after the peer has gone away on macOS.
	if file, ok := vpn.(*os.File); ok {
		connection, err := net.FileConn(file)
		if err != nil {
			return nil, err
		}
		_ = file.Close()
		vpn = connection
	}
	s := stack.New(stack.Options{
		NetworkProtocols:   []stack.NetworkProtocolFactory{ipv4.NewProtocol, ipv6.NewProtocol},
		TransportProtocols: []stack.TransportProtocolFactory{tcp.NewProtocol, udp.NewProtocol},
	})
	link := channel.New(256, uint32(config.MTU), "")
	if err := s.CreateNIC(1, link); err != nil {
		s.Close()
		link.Close()
		_ = vpn.Close()
		return nil, fmt.Errorf("create VPN network stack: %s", err)
	}
	for _, prefix := range []*netip.Prefix{config.IPv4Address, config.IPv6Address} {
		if prefix == nil {
			continue
		}
		address := tcpip.ProtocolAddress{
			Protocol:          vpnNetworkProtocol(prefix.Addr()),
			AddressWithPrefix: tcpip.AddressWithPrefix{Address: tcpip.AddrFromSlice(prefix.Addr().AsSlice()), PrefixLen: prefix.Bits()},
		}
		if err := s.AddProtocolAddress(1, address, stack.AddressProperties{}); err != nil {
			s.Close()
			link.Close()
			_ = vpn.Close()
			return nil, fmt.Errorf("assign VPN network address: %s", err)
		}
	}
	s.SetRouteTable([]tcpip.Route{
		{Destination: header.IPv4EmptySubnet, NIC: 1},
		{Destination: header.IPv6EmptySubnet, NIC: 1},
	})
	ctx, cancel := context.WithCancel(ctx)
	result := &vpnSocketStack{stack: s, link: link, vpn: vpn, cancel: cancel, done: make(chan error, 2)}
	result.workers.Add(2)
	go func() { defer result.workers.Done(); result.done <- result.receive(ctx, int(config.MTU)) }()
	go func() { defer result.workers.Done(); result.done <- result.transmit(ctx) }()
	return result, nil
}

func (s *vpnSocketStack) Close() {
	s.closeOnce.Do(func() {
		s.cancel()
		interruptVPN(s.vpn)
		s.workers.Wait()
		s.stack.Close()
		s.stack.Wait()
		s.link.Close()
	})
}

func (s *vpnSocketStack) receive(ctx context.Context, mtu int) error {
	packet := make([]byte, mtu+1)
	for {
		n, err := s.vpn.Read(packet)
		if err != nil {
			return err
		}
		if n == 0 {
			return io.EOF
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if n > mtu {
			return errors.New("VPN packet exceeds negotiated MTU")
		}
		destination, err := packetDestination(packet[:n])
		if err != nil {
			return err
		}
		pkt := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(append([]byte(nil), packet[:n]...))})
		s.link.InjectInbound(vpnNetworkProtocol(destination), pkt)
		pkt.DecRef()
	}
}

func (s *vpnSocketStack) transmit(ctx context.Context) error {
	for {
		pkt := s.link.ReadContext(ctx)
		if pkt == nil {
			return ctx.Err()
		}
		view := pkt.ToView()
		n, err := writeVPNPacket(ctx, s.vpn, view.AsSlice())
		length := view.Size()
		view.Release()
		pkt.DecRef()
		if err != nil {
			return err
		}
		if n != length {
			return io.ErrShortWrite
		}
	}
}

func (s *vpnSocketStack) dialTCP(ctx context.Context, address netip.AddrPort) (net.Conn, error) {
	return gonet.DialContextTCP(ctx, s.stack, vpnSocketAddress(address), vpnNetworkProtocol(address.Addr()))
}

func (s *vpnSocketStack) dialDNS(ctx context.Context, network string, address netip.Addr) (net.Conn, error) {
	remote := vpnSocketAddress(netip.AddrPortFrom(address, 53))
	if network == "tcp" {
		return s.dialTCP(ctx, netip.AddrPortFrom(address, 53))
	}
	return gonet.DialUDP(s.stack, nil, &remote, vpnNetworkProtocol(address))
}

func vpnSocketAddress(address netip.AddrPort) tcpip.FullAddress {
	return tcpip.FullAddress{NIC: 1, Addr: tcpip.AddrFromSlice(address.Addr().AsSlice()), Port: address.Port()}
}

func vpnNetworkProtocol(address netip.Addr) tcpip.NetworkProtocolNumber {
	if address.Is4() {
		return ipv4.ProtocolNumber
	}
	return ipv6.ProtocolNumber
}
