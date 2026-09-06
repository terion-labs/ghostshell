package ethernet

import (
	"bytes"
	"context"
	"io"
	"net"
	"net/netip"
	"sync"
	"testing"
	"time"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
	"gvisor.dev/gvisor/pkg/buffer"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/adapters/gonet"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/link/channel"
	ethernetlink "gvisor.dev/gvisor/pkg/tcpip/link/ethernet"
	"gvisor.dev/gvisor/pkg/tcpip/network/arp"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv6"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/icmp"
	"gvisor.dev/gvisor/pkg/tcpip/transport/tcp"
	"gvisor.dev/gvisor/pkg/tcpip/transport/udp"
)

func TestEthernetRoutesTCPUDPAndDNS(t *testing.T) {
	for _, v6 := range []bool{false, true} {
		for _, sameAddress := range []bool{false, true} {
			name := "ipv4"
			if v6 {
				name = "ipv6"
			}
			if sameAddress {
				name += "-proxy-address"
			}
			t.Run(name, func(t *testing.T) {
				h := newNetwork(t, v6, sameAddress)
				for _, transport := range []string{"tcp", "udp"} {
					for _, dns := range []bool{false, true} {
						port := uint16(8443)
						destination := h.remote
						if dns {
							port = 53
							destination = h.gateway
						}
						t.Run(transport+"-"+destination.String(), func(t *testing.T) { h.echo(t, transport, destination, port) })
					}
				}
				h.mu.Lock()
				defer h.mu.Unlock()
				if h.arp == 0 && !v6 {
					t.Fatal("IPv4 traffic did not exercise ARP")
				}
				if h.ndp == 0 && v6 {
					t.Fatal("IPv6 traffic did not exercise NDP")
				}
				if len(h.packets) == 0 {
					t.Fatal("no uplink packets")
				}
				for _, packet := range h.packets {
					if len(packet) > MTU {
						t.Fatalf("packet exceeds provider MTU: %d", len(packet))
					}
					var source, dest tcpip.Address
					if v6 {
						ip := header.IPv6(packet)
						source = ip.SourceAddress()
						dest = ip.DestinationAddress()
					} else {
						ip := header.IPv4(packet)
						source = ip.SourceAddress()
						dest = ip.DestinationAddress()
					}
					if source != tcpip.AddrFromSlice(h.assigned.AsSlice()) || dest != tcpip.AddrFromSlice(h.remote.AsSlice()) {
						t.Fatalf("unexpected translated packet %s -> %s", source, dest)
					}
				}
			})
		}
	}
}

type network struct {
	ctx                       context.Context
	router                    *router
	guest, server             *stack.Stack
	guestLink, serverLink     *channel.Endpoint
	remote, gateway, assigned netip.Addr
	workers                   sync.WaitGroup
	mu                        sync.Mutex
	packets                   [][]byte
	frames                    [][]byte
	arp, ndp                  int
}

func newNetwork(t *testing.T, v6, sameAddress bool) *network {
	t.Helper()
	guest, gateway, assigned, remote := guest4, gateway4, netip.MustParseAddr("10.22.0.2"), netip.MustParseAddr("192.0.2.53")
	if v6 {
		guest, gateway, assigned, remote = guest6, gateway6, netip.MustParseAddr("fd00:abcd::2"), netip.MustParseAddr("2001:db8::53")
	}
	if sameAddress {
		assigned = guest
	}
	prefix := netip.PrefixFrom(assigned, assigned.BitLen())
	config := protocol.NetworkConfiguration{MTU: MTU, DNSServers: []netip.Addr{remote}}
	if v6 {
		config.IPv6Address = &prefix
	} else {
		config.IPv4Address = &prefix
	}
	r, err := newRouter(config)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	h := &network{ctx: ctx, router: r, remote: remote, gateway: gateway, assigned: assigned}
	h.guest, h.guestLink = testStack(t, guest, true)
	h.server, h.serverLink = testStack(t, remote, false)
	empty := header.IPv4EmptySubnet
	if v6 {
		empty = header.IPv6EmptySubnet
	}
	h.guest.SetRouteTable([]tcpip.Route{{Destination: empty, Gateway: tcpip.AddrFromSlice(gateway.AsSlice()), NIC: 1}})
	h.server.SetRouteTable([]tcpip.Route{{Destination: empty, NIC: 1}})
	h.workers.Add(4)
	go func() {
		defer h.workers.Done()
		for {
			frame := readLink(ctx, h.guestLink)
			if frame == nil {
				return
			}
			h.mu.Lock()
			kind := header.Ethernet(frame).Type()
			if kind == header.ARPProtocolNumber {
				h.arp++
			}
			if kind == header.IPv6ProtocolNumber && len(frame) > 54 && frame[20] == 58 {
				h.ndp++
			}
			h.mu.Unlock()
			r.injectFrame(frame)
		}
	}()
	go func() {
		defer h.workers.Done()
		for {
			frame := readLink(ctx, r.guest)
			if frame == nil {
				return
			}
			h.mu.Lock()
			h.frames = append(h.frames, append([]byte(nil), frame...))
			h.mu.Unlock()
			inject(h.guestLink, 0, frame)
		}
	}()
	go func() {
		defer h.workers.Done()
		for {
			packet := readLink(ctx, r.provider)
			if packet == nil {
				return
			}
			h.mu.Lock()
			h.packets = append(h.packets, append([]byte(nil), packet...))
			h.mu.Unlock()
			inject(h.serverLink, networkProtocol(remote), packet)
		}
	}()
	go func() {
		defer h.workers.Done()
		for {
			packet := readLink(ctx, h.serverLink)
			if packet == nil {
				return
			}
			r.injectPacket(packet)
		}
	}()
	t.Cleanup(func() {
		cancel()
		h.workers.Wait()
		r.close()
		h.guest.Close()
		h.server.Close()
		h.guest.Wait()
		h.server.Wait()
		h.guestLink.Close()
		h.serverLink.Close()
	})
	return h
}

func testStack(t *testing.T, address netip.Addr, l2 bool) (*stack.Stack, *channel.Endpoint) {
	t.Helper()
	s := stack.New(stack.Options{NetworkProtocols: []stack.NetworkProtocolFactory{arp.NewProtocol, ipv4.NewProtocol, ipv6.NewProtocol}, TransportProtocols: []stack.TransportProtocolFactory{tcp.NewProtocol, udp.NewProtocol, icmp.NewProtocol4, icmp.NewProtocol6}})
	mtu := uint32(MTU)
	if l2 {
		mtu += 14
	}
	link := channel.New(256, mtu, "\x02\x47\x53\x4e\x57\x02")
	var endpoint stack.LinkEndpoint = link
	if l2 {
		endpoint = ethernetlink.New(link)
	}
	if err := s.CreateNIC(1, endpoint); err != nil {
		t.Fatal(err)
	}
	if err := addAddress(s, 1, netip.PrefixFrom(address, address.BitLen())); err != nil {
		t.Fatal(err)
	}
	return s, link
}

func (h *network) echo(t *testing.T, transport string, destination netip.Addr, port uint16) {
	t.Helper()
	serverAddress := tcpip.FullAddress{NIC: 1, Addr: tcpip.AddrFromSlice(h.remote.AsSlice()), Port: port}
	clientAddress := tcpip.FullAddress{NIC: 1, Addr: tcpip.AddrFromSlice(destination.AsSlice()), Port: port}
	number := networkProtocol(h.remote)
	ctx, cancel := context.WithTimeout(h.ctx, 10*time.Second)
	defer cancel()
	var client net.Conn
	var server net.Conn
	if transport == "tcp" {
		listener, err := gonet.ListenTCP(h.server, serverAddress, number)
		if err != nil {
			t.Fatal(err)
		}
		defer listener.Close()
		client, err = gonet.DialContextTCP(ctx, h.guest, clientAddress, number)
		if err != nil {
			t.Fatal(err)
		}
		defer client.Close()
		server, err = listener.Accept()
		if err != nil {
			t.Fatal(err)
		}
	} else {
		var err error
		server, err = gonet.DialUDP(h.server, &serverAddress, nil, number)
		if err != nil {
			t.Fatal(err)
		}
		client, err = gonet.DialUDP(h.guest, nil, &clientAddress, number)
		if err != nil {
			t.Fatal(err)
		}
		defer client.Close()
	}
	defer server.Close()
	deadline := time.Now().Add(10 * time.Second)
	_ = client.SetDeadline(deadline)
	_ = server.SetDeadline(deadline)
	want := bytes.Repeat([]byte("workspace routed payload"), 100)
	if _, err := client.Write(want); err != nil {
		t.Fatal(err)
	}
	buffer := make([]byte, len(want))
	if datagram, ok := server.(*gonet.UDPConn); ok {
		n, peer, err := datagram.ReadFrom(buffer)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := datagram.WriteTo(buffer[:n], peer); err != nil {
			t.Fatal(err)
		}
	} else {
		if _, err := io.ReadFull(server, buffer[:len(want)]); err != nil {
			t.Fatal(err)
		}
		if _, err := server.Write(buffer[:len(want)]); err != nil {
			t.Fatal(err)
		}
	}
	n, err := io.ReadFull(client, buffer)
	if err != nil {
		t.Fatal(err)
	}
	if string(buffer[:n]) != string(want) {
		t.Fatalf("echo got %q", buffer[:n])
	}
}

func readLink(ctx context.Context, link *channel.Endpoint) []byte {
	pkt := link.ReadContext(ctx)
	if pkt == nil {
		return nil
	}
	view := pkt.ToView()
	data := append([]byte(nil), view.AsSlice()...)
	view.Release()
	pkt.DecRef()
	return data
}

func inject(link *channel.Endpoint, number tcpip.NetworkProtocolNumber, data []byte) {
	pkt := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(data)})
	link.InjectInbound(number, pkt)
	pkt.DecRef()
}
