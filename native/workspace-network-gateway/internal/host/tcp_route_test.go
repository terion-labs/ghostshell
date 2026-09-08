package host

import (
	"context"
	"errors"
	"io"
	"net"
	"strings"
	"testing"
	"time"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
	"github.com/xjasonlyu/tun2socks/v2/proxy"
	"gvisor.dev/gvisor/pkg/buffer"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/adapters/gonet"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/link/channel"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/tcp"
)

func TestTCPConnectWaitsForRouteAndPreservesRefusal(t *testing.T) {
	entered := make(chan struct{})
	release := make(chan struct{})
	route := &tcpFixtureProxy{dial: func(ctx context.Context, metadata *M.Metadata) (net.Conn, error) {
		if metadata.DstIP.String() != "198.51.100.10" || metadata.DstPort != 1433 {
			t.Errorf("changed destination: %v", metadata)
		}
		close(entered)
		select {
		case <-release:
			return nil, errors.New("upstream refused")
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}}
	client, target, closeStacks := tcpTestStacks(t, route)
	defer closeStacks()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	result := make(chan error, 1)
	go func() {
		connection, err := gonet.DialContextTCP(ctx, client, target, ipv4.ProtocolNumber)
		if connection != nil {
			connection.Close()
		}
		result <- err
	}()
	select {
	case <-entered:
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	select {
	case err := <-result:
		t.Fatalf("guest TCP completed before the selected route: %v", err)
	case <-time.After(50 * time.Millisecond):
	}
	close(release)
	select {
	case err := <-result:
		if err == nil || !strings.Contains(err.Error(), "refused") {
			t.Fatalf("expected connect refusal, not post-connect EOF: %v", err)
		}
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
}

func TestTCPRouteCarriesBytesAndClosesBothPeersOnShutdown(t *testing.T) {
	remote, echo := net.Pipe()
	finished := make(chan struct{})
	go func() { defer close(finished); defer echo.Close(); _, _ = io.Copy(echo, echo) }()
	client, target, closeStacks := tcpTestStacks(t, &tcpFixtureProxy{dial: func(context.Context, *M.Metadata) (net.Conn, error) { return remote, nil }})
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	connection, err := gonet.DialContextTCP(ctx, client, target, ipv4.ProtocolNumber)
	if err != nil {
		closeStacks()
		t.Fatal(err)
	}
	_ = connection.SetDeadline(time.Now().Add(5 * time.Second))
	if _, err = connection.Write([]byte("original-destination")); err != nil {
		t.Fatal(err)
	}
	got := make([]byte, len("original-destination"))
	if _, err = io.ReadFull(connection, got); err != nil || string(got) != "original-destination" {
		t.Fatalf("echo: %q %v", got, err)
	}
	closeStacks()
	connection.Close()
	select {
	case <-finished:
	case <-ctx.Done():
		t.Fatal("upstream survived route shutdown")
	}
}

type tcpFixtureProxy struct {
	dial func(context.Context, *M.Metadata) (net.Conn, error)
}

func (value *tcpFixtureProxy) DialContext(ctx context.Context, metadata *M.Metadata) (net.Conn, error) {
	return value.dial(ctx, metadata)
}
func (*tcpFixtureProxy) DialUDP(*M.Metadata) (net.PacketConn, error) {
	return nil, errors.ErrUnsupported
}

func tcpTestStacks(t *testing.T, upstream proxy.Proxy) (*stack.Stack, tcpip.FullAddress, func()) {
	t.Helper()
	makeStack := func(address [4]byte) (*stack.Stack, *channel.Endpoint) {
		s := stack.New(stack.Options{NetworkProtocols: []stack.NetworkProtocolFactory{ipv4.NewProtocol}, TransportProtocols: []stack.TransportProtocolFactory{tcp.NewProtocol}})
		link := channel.New(128, 1500, "")
		if err := s.CreateNIC(1, link); err != nil {
			t.Fatal(err)
		}
		if err := s.AddProtocolAddress(1, tcpip.ProtocolAddress{Protocol: ipv4.ProtocolNumber, AddressWithPrefix: tcpip.AddressWithPrefix{Address: tcpip.AddrFrom4(address), PrefixLen: 32}}, stack.AddressProperties{}); err != nil {
			t.Fatal(err)
		}
		s.SetRouteTable([]tcpip.Route{{Destination: header.IPv4EmptySubnet, NIC: 1}})
		return s, link
	}
	client, a := makeStack([4]byte{100, 64, 0, 2})
	server, b := makeStack([4]byte{198, 51, 100, 10})
	forwarder := installTCPRoute(server, upstream)
	ctx, cancel := context.WithCancel(context.Background())
	finished := make(chan struct{}, 2)
	pump := func(from, to *channel.Endpoint) {
		defer func() { finished <- struct{}{} }()
		for {
			packet := from.ReadContext(ctx)
			if packet == nil {
				return
			}
			wire := packet.ToBuffer()
			incoming := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(wire.Flatten())})
			to.InjectInbound(ipv4.ProtocolNumber, incoming)
			incoming.DecRef()
			wire.Release()
			packet.DecRef()
		}
	}
	go pump(a, b)
	go pump(b, a)
	return client, tcpip.FullAddress{Addr: tcpip.AddrFrom4([4]byte{198, 51, 100, 10}), Port: 1433}, func() {
		forwarder.stop()
		cancel()
		<-finished
		<-finished
		server.Close()
		client.Close()
		forwarder.active.Wait()
		server.Wait()
		client.Wait()
	}
}
