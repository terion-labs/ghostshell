package host

import (
	"context"
	"io"
	"net"
	"net/netip"
	"sync"
	"time"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
	"github.com/xjasonlyu/tun2socks/v2/proxy"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/adapters/gonet"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/tcp"
	"gvisor.dev/gvisor/pkg/waiter"
)

// A successful guest connect must mean that the selected route accepted the
// destination. A SYN-ACK before SOCKS CONNECT turns refusal into a later protocol
// EOF, breaking ordinary driver failover. gVisor's public forwarder API lets us
// preserve this distinction without modifying either driver or network library.
type tcpRouteForwarder struct {
	ctx      context.Context
	cancel   context.CancelFunc
	upstream proxy.Proxy
	mu       sync.Mutex
	closed   bool
	active   sync.WaitGroup
}

func installTCPRoute(s *stack.Stack, upstream proxy.Proxy) *tcpRouteForwarder {
	ctx, cancel := context.WithCancel(context.Background())
	route := &tcpRouteForwarder{ctx: ctx, cancel: cancel, upstream: upstream}
	forwarder := tcp.NewForwarder(s, 0, 2048, route.connect)
	s.SetTransportProtocolHandler(tcp.ProtocolNumber, forwarder.HandlePacket)
	return route
}

func (route *tcpRouteForwarder) connect(request *tcp.ForwarderRequest) {
	route.mu.Lock()
	if route.closed {
		route.mu.Unlock()
		request.Complete(false)
		return
	}
	route.active.Add(1)
	route.mu.Unlock()
	defer route.active.Done()

	id := request.ID()
	source, sourceOK := netip.AddrFromSlice(id.RemoteAddress.AsSlice())
	destination, destinationOK := netip.AddrFromSlice(id.LocalAddress.AsSlice())
	if !sourceOK || !destinationOK {
		request.Complete(true)
		return
	}
	dial, cancel := context.WithTimeout(route.ctx, connectTimeout)
	remote, err := route.upstream.DialContext(dial, &M.Metadata{
		Network: M.TCP, SrcIP: source, SrcPort: id.RemotePort,
		DstIP: destination, DstPort: id.LocalPort,
	})
	cancel()
	if err != nil {
		request.Complete(true)
		return
	}
	defer remote.Close()
	if route.ctx.Err() != nil {
		request.Complete(false)
		return
	}
	var queue waiter.Queue
	endpoint, endpointError := request.CreateEndpoint(&queue)
	if endpointError != nil {
		request.Complete(true)
		return
	}
	request.Complete(false)
	endpoint.SocketOptions().SetKeepAlive(true)
	idle := tcpip.KeepaliveIdleOption(time.Minute)
	interval := tcpip.KeepaliveIntervalOption(30 * time.Second)
	if endpoint.SetSockOpt(&idle) != nil || endpoint.SetSockOpt(&interval) != nil {
		endpoint.Close()
		return
	}
	guest := gonet.NewTCPConn(&queue, endpoint)
	defer guest.Close()
	stop := context.AfterFunc(route.ctx, func() { guest.Close(); remote.Close() })
	defer stop()
	finished := make(chan struct{})
	go func() { forwardTCPHalf(remote, guest); close(finished) }()
	forwardTCPHalf(guest, remote)
	<-finished
}

func forwardTCPHalf(destination, source net.Conn) {
	_, err := io.Copy(destination, source)
	if err != nil {
		destination.Close()
		source.Close()
		return
	}
	if writer, ok := destination.(interface{ CloseWrite() error }); ok {
		_ = writer.CloseWrite()
	}
	// Preserve request half-close while bounding a peer that never finishes its
	// response. Route revocation closes both directions immediately.
	_ = destination.SetReadDeadline(time.Now().Add(15 * time.Second))
}

func (route *tcpRouteForwarder) stop() {
	route.mu.Lock()
	route.closed = true
	route.mu.Unlock()
	route.cancel()
}

// Packet dispatch starts only after both TCP and UDP handlers are installed.
// core.CreateStack attaches its endpoint before returning; gating its first
// read avoids a race with the library's default, eager SYN-ACK handler.
type startingPacketStream struct {
	io.ReadWriteCloser
	ready chan struct{}
}

func (stream *startingPacketStream) Read(buffer []byte) (int, error) {
	<-stream.ready
	return stream.ReadWriteCloser.Read(buffer)
}
