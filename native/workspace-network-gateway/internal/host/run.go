package host

import (
	"bufio"
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/xjasonlyu/tun2socks/v2/core"
	"github.com/xjasonlyu/tun2socks/v2/core/device/iobased"
	gatewaylog "github.com/xjasonlyu/tun2socks/v2/log"
	M "github.com/xjasonlyu/tun2socks/v2/metadata"
	"github.com/xjasonlyu/tun2socks/v2/proxy"
	directproxy "github.com/xjasonlyu/tun2socks/v2/proxy/direct"
	httpproxy "github.com/xjasonlyu/tun2socks/v2/proxy/http"
	"github.com/xjasonlyu/tun2socks/v2/proxy/socks5"
	"github.com/xjasonlyu/tun2socks/v2/tunnel"
	"github.com/xjasonlyu/tun2socks/v2/tunnel/statistic"
	"gvisor.dev/gvisor/pkg/tcpip/stack"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

const connectTimeout = 10 * time.Second

var (
	guestIPv4 = netip.MustParsePrefix("100.64.0.2/30")
	guestIPv6 = netip.MustParsePrefix("fd00:4753:4e57::2/126")
)

type Options struct {
	SocketPath        string
	Mode              string
	UpstreamHost      string
	UpstreamPort      uint16
	MTU               uint16
	DNSServers        []netip.Addr
	DNSOverHTTPS      bool
	ResolveProxyNames bool
	AllowUDPAssociate bool
	KeyInput          io.Reader
	Ready             io.Writer
}

type Capabilities struct {
	Families  []string
	Protocols []string
	MTU       uint16
}

func (value Capabilities) ReadinessLine() string {
	return fmt.Sprintf("READY v1 families=%s protocols=%s mtu=%d", strings.Join(value.Families, ","), strings.Join(value.Protocols, ","), value.MTU)
}

func Run(ctx context.Context, options Options) error {
	if ctx == nil || options.KeyInput == nil || options.Ready == nil {
		return errors.New("context, key input, and readiness output are required")
	}
	if !strings.HasPrefix(options.SocketPath, "/") {
		return errors.New("host socket path must be absolute")
	}
	if options.MTU < 1280 {
		return errors.New("--mtu must be at least 1280")
	}
	if options.ResolveProxyNames {
		// This is a virtual service owned by this gateway, not an external
		// resolver. DNS packets are answered by the per-VM name table below.
		options.DNSServers = []netip.Addr{proxyNamesIPv4.Addr()}
	}
	configuration, err := networkConfiguration(options)
	if err != nil {
		return err
	}
	upstream, capabilities, err := newUpstream(options)
	if err != nil {
		return err
	}
	var authenticationKey []byte
	var credentials *proxyNameCredentials
	if options.ResolveProxyNames {
		authenticationKey, credentials, err = readServiceAuthentication(options.KeyInput)
	} else {
		authenticationKey, err = readAuthenticationKey(options.KeyInput)
	}
	if err != nil {
		return err
	}
	defer clearBytes(authenticationKey)
	if options.Mode != "direct" {
		dnsProxy := newTCPDNSProxy(upstream, options.DNSServers, options.DNSOverHTTPS)
		if options.ResolveProxyNames {
			dnsProxy.names = newProxyDNSNames(net.JoinHostPort(options.UpstreamHost, strconv.Itoa(int(options.UpstreamPort))))
			dnsProxy.names.credentials = credentials
		}
		dnsProxy.allowUDP = options.AllowUDPAssociate
		upstream = dnsProxy
		if dnsProxy.doh != nil {
			defer dnsProxy.doh.CloseIdleConnections()
		}
	}
	dialer := net.Dialer{Timeout: connectTimeout}
	connection, err := dialer.DialContext(ctx, "unix", options.SocketPath)
	if err != nil {
		return fmt.Errorf("connect to guest packet socket: %w", err)
	}
	defer connection.Close()
	stopCancellation := context.AfterFunc(ctx, func() { _ = connection.Close() })
	defer stopCancellation()
	if err := connection.SetDeadline(time.Now().Add(connectTimeout)); err != nil {
		return err
	}
	channel, err := protocol.ConnectHost(connection, authenticationKey, configuration)
	clearBytes(authenticationKey)
	if err != nil {
		return fmt.Errorf("establish guest packet channel: %w", err)
	}
	if err := connection.SetDeadline(time.Time{}); err != nil {
		return err
	}
	stream := &packetStream{channel: channel, maximumPacketSize: int(options.MTU)}
	if options.Mode == "direct" {
		stream.echo = newEchoForwarder(channel, int(options.MTU))
	} else {
		// tun2socks locally answers echo requests. That would falsely claim that
		// an unreachable destination was alive and bypass the selected proxy's
		// actual capabilities, so proxy routes discard echo requests instead.
		stream.dropEcho = true
	}
	dataPlane, err := newDataPlane(stream, uint32(options.MTU), upstream)
	if err != nil {
		channel.Close()
		return fmt.Errorf("start userspace network stack: %w", err)
	}
	defer dataPlane.Close()

	if _, err := fmt.Fprintln(options.Ready, capabilities.ReadinessLine()); err != nil {
		return fmt.Errorf("announce readiness: %w", err)
	}
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-dataPlane.done:
		return errors.New("guest packet channel stopped")
	}
}

func networkConfiguration(options Options) (protocol.NetworkConfiguration, error) {
	if len(options.DNSServers) == 0 || len(options.DNSServers) > 4 {
		return protocol.NetworkConfiguration{}, errors.New("supply between one and four --dns addresses")
	}
	dns := make([]netip.Addr, len(options.DNSServers))
	for index, server := range options.DNSServers {
		if !server.IsValid() || server.Zone() != "" || server.IsUnspecified() || server.IsMulticast() ||
			server.IsLoopback() || server.IsLinkLocalUnicast() {
			return protocol.NetworkConfiguration{}, errors.New("--dns must contain routable unicast IP addresses")
		}
		dns[index] = server.Unmap()
	}
	return protocol.NetworkConfiguration{
		InterfaceName: "gsnet0",
		MTU:           options.MTU,
		IPv4Address:   prefixPointer(guestIPv4),
		IPv6Address:   prefixPointer(guestIPv6),
		DNSServers:    dns,
	}, nil
}

func prefixPointer(value netip.Prefix) *netip.Prefix { return &value }

func newUpstream(options Options) (proxy.Proxy, Capabilities, error) {
	if options.ResolveProxyNames && (options.Mode != "socks5" || options.DNSOverHTTPS || options.AllowUDPAssociate) {
		return nil, Capabilities{}, errors.New("proxy-name resolution requires a TCP-only SOCKS5 route without DNS-over-HTTPS")
	}
	if options.AllowUDPAssociate && options.Mode != "socks5" {
		return nil, Capabilities{}, errors.New("UDP association requires a SOCKS5 route")
	}
	capabilities := Capabilities{Families: []string{"ipv4", "ipv6"}, MTU: options.MTU}
	address := ""
	if options.Mode != "direct" {
		if options.UpstreamHost == "" || options.UpstreamPort == 0 {
			return nil, Capabilities{}, errors.New("proxy mode requires --upstream-host and --upstream-port")
		}
		address = net.JoinHostPort(options.UpstreamHost, strconv.Itoa(int(options.UpstreamPort)))
	}
	switch options.Mode {
	case "direct":
		value, err := directproxy.New()
		capabilities.Protocols = []string{"tcp", "udp"}
		return value, capabilities, err
	case "socks5":
		value, err := socks5.New(address, "", "")
		if err == nil && options.AllowUDPAssociate {
			udpProxy := &loopbackSOCKSUDP{Proxy: value, address: address}
			association, associateError := udpProxy.DialUDP(nil)
			if associateError != nil {
				return nil, Capabilities{}, associateError
			}
			association.Close()
			capabilities.Protocols = []string{"tcp", "udp"}
			return udpProxy, capabilities, nil
		}
		// The local authenticated adapter supports CONNECT, not UDP ASSOCIATE.
		capabilities.Protocols = []string{"tcp"}
		return value, capabilities, err
	case "http":
		value, err := httpproxy.New(address, "", "")
		capabilities.Protocols = []string{"tcp"}
		return value, capabilities, err
	case "https":
		capabilities.Protocols = []string{"tcp"}
		return &httpsConnectProxy{address: address}, capabilities, nil
	default:
		return nil, Capabilities{}, errors.New("--mode must be direct, socks5, http, or https")
	}
}

func readAuthenticationKey(input io.Reader) ([]byte, error) {
	key := make([]byte, protocol.AuthenticationKeyLength)
	if _, err := io.ReadFull(input, key); err != nil {
		clearBytes(key)
		return nil, fmt.Errorf("read authentication key: %w", err)
	}
	var extra [1]byte
	if count, err := input.Read(extra[:]); count != 0 || !errors.Is(err, io.EOF) {
		clearBytes(key)
		return nil, errors.New("authentication key input must contain exactly 32 bytes followed by EOF")
	}
	return key, nil
}

type packetStream struct {
	channel           *protocol.Channel
	maximumPacketSize int
	echo              *echoForwarder
	dropEcho          bool
}

func (stream *packetStream) Read(buffer []byte) (int, error) {
	for {
		packet, err := stream.channel.ReceivePacket()
		if err != nil {
			return 0, err
		}
		if stream.interceptEcho(packet) {
			clearBytes(packet)
			continue
		}
		defer clearBytes(packet)
		if len(packet) > stream.maximumPacketSize || len(packet) > len(buffer) {
			return 0, io.ErrShortBuffer
		}
		return copy(buffer, packet), nil
	}
}

func (stream *packetStream) interceptEcho(packet []byte) bool {
	if stream.echo != nil {
		return stream.echo.Forward(packet)
	}
	if !stream.dropEcho {
		return false
	}
	_, echoRequest := parseEchoRequest(packet)
	return echoRequest
}

func (stream *packetStream) Write(packet []byte) (int, error) {
	if len(packet) > stream.maximumPacketSize {
		return 0, io.ErrShortBuffer
	}
	if err := stream.channel.SendPacket(packet); err != nil {
		return 0, err
	}
	return len(packet), nil
}

func (stream *packetStream) Close() error {
	if stream.echo != nil {
		stream.echo.Close()
	}
	stream.channel.Close()
	return nil
}

type dataPlane struct {
	stream   io.Closer
	endpoint *iobased.Endpoint
	stack    *stack.Stack
	tunnel   *tunnel.Tunnel
	done     chan struct{}
	once     sync.Once
	tcpRoute *tcpRouteForwarder
}

func newDataPlane(stream io.ReadWriteCloser, mtu uint32, upstream proxy.Proxy) (*dataPlane, error) {
	// The upstream stack logs destination addresses at info level. Keep the
	// helper silent so workspace traffic metadata does not enter application logs.
	gatewaylog.SetLogger(gatewaylog.Must(gatewaylog.NewLeveled(gatewaylog.SilentLevel)))
	starting := &startingPacketStream{ReadWriteCloser: stream, ready: make(chan struct{})}
	endpoint, err := iobased.New(starting, mtu, 0)
	if err != nil {
		return nil, err
	}
	transport := tunnel.New(upstream, statistic.DefaultManager)
	transport.ProcessAsync()
	networkStack, err := core.CreateStack(&core.Config{LinkEndpoint: endpoint, TransportHandler: transport})
	if err != nil {
		close(starting.ready)
		stream.Close()
		transport.Close()
		return nil, err
	}
	result := &dataPlane{stream: stream, endpoint: endpoint, stack: networkStack, tunnel: transport, done: make(chan struct{}),
		tcpRoute: installTCPRoute(networkStack, upstream)}
	close(starting.ready)
	go func() {
		endpoint.Wait()
		close(result.done)
	}()
	return result, nil
}

func (plane *dataPlane) Close() {
	plane.once.Do(func() {
		plane.tcpRoute.stop()
		_ = plane.stream.Close()
		<-plane.done
		plane.stack.Close()
		plane.tcpRoute.active.Wait()
		plane.stack.Wait()
		plane.tunnel.Close()
	})
}

type httpsConnectProxy struct{ address string }

func (upstream *httpsConnectProxy) DialContext(ctx context.Context, metadata *M.Metadata) (net.Conn, error) {
	tlsDialer := tls.Dialer{NetDialer: &net.Dialer{Timeout: connectTimeout}}
	connection, err := tlsDialer.DialContext(ctx, "tcp", upstream.address)
	if err != nil {
		return nil, err
	}
	return connectHTTPSTunnel(ctx, connection, metadata)
}

// TLS establishment is only the first half of connecting to an HTTPS proxy.
// Its CONNECT response is also bounded and cancellable; successful tunnels
// shed the handshake deadline before being handed to the packet data plane.
func connectHTTPSTunnel(ctx context.Context, connection net.Conn, metadata *M.Metadata) (net.Conn, error) {
	stop := context.AfterFunc(ctx, func() { _ = connection.Close() })
	defer stop()
	if err := connection.SetDeadline(time.Now().Add(connectTimeout)); err != nil {
		_ = connection.Close()
		return nil, err
	}
	request := &http.Request{Method: http.MethodConnect, URL: &url.URL{Host: metadata.DestinationAddress()}, Host: metadata.DestinationAddress()}
	if err := request.Write(connection); err != nil {
		_ = connection.Close()
		return nil, err
	}
	reader := bufio.NewReader(connection)
	response, err := http.ReadResponse(reader, request)
	if err != nil {
		_ = connection.Close()
		return nil, err
	}
	if response.StatusCode != http.StatusOK {
		_ = response.Body.Close()
		_ = connection.Close()
		return nil, fmt.Errorf("HTTPS CONNECT failed with status %s", response.Status)
	}
	if err := connection.SetDeadline(time.Time{}); err != nil {
		_ = connection.Close()
		return nil, err
	}
	return &bufferedConnection{Conn: connection, reader: reader}, nil
}

func (*httpsConnectProxy) DialUDP(*M.Metadata) (net.PacketConn, error) {
	return nil, errors.ErrUnsupported
}

type bufferedConnection struct {
	net.Conn
	reader *bufio.Reader
}

func (connection *bufferedConnection) Read(buffer []byte) (int, error) {
	return connection.reader.Read(buffer)
}

func clearBytes(value []byte) {
	for index := range value {
		value[index] = 0
	}
}
