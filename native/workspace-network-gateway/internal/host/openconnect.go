package host

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	directproxy "github.com/xjasonlyu/tun2socks/v2/proxy/direct"
	"golang.org/x/sys/unix"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

type OpenConnectOptions struct {
	SocketPath  string
	KeyFilePath string
	Environment func(string) string
	Ready       io.Writer
}

func RunOpenConnect(ctx context.Context, options OpenConnectOptions) error {
	if ctx == nil || options.Environment == nil || options.Ready == nil {
		return errors.New("context, OpenConnect environment, and readiness output are required")
	}
	if !strings.HasPrefix(options.SocketPath, "/") || !strings.HasPrefix(options.KeyFilePath, "/") {
		return errors.New("guest socket and key file paths must be absolute")
	}
	configuration, families, err := openConnectConfiguration(options.Environment)
	if err != nil {
		return err
	}
	routePolicy, err := parseOpenConnectRoutePolicy(options.Environment, configuration)
	if err != nil {
		return err
	}
	vpn, err := openVPNFile(options.Environment("VPNFD"))
	if err != nil {
		return err
	}
	defer vpn.Close()

	authenticationKey, err := readKeyFile(options.KeyFilePath)
	if err != nil {
		return err
	}
	defer clearBytes(authenticationKey)
	dialer := net.Dialer{Timeout: connectTimeout}
	connection, err := dialer.DialContext(ctx, "unix", options.SocketPath)
	if err != nil {
		return fmt.Errorf("connect to guest packet socket: %w", err)
	}
	defer connection.Close()
	channel, err := protocol.ConnectHost(connection, authenticationKey, configuration)
	clearBytes(authenticationKey)
	if err != nil {
		return fmt.Errorf("establish OpenConnect guest packet channel: %w", err)
	}
	defer channel.Close()

	protocols := []string{"tcp", "udp", "control", "other"}
	if routePolicy.usesDirectPath() {
		// The direct side of a split route uses the userspace TCP/UDP stack.
		// Do not claim arbitrary raw protocols just because the VPN side has them.
		protocols = []string{"tcp", "udp"}
	}
	capabilities := Capabilities{Families: families, Protocols: protocols, MTU: configuration.MTU}
	return pumpVPNFD(ctx, vpn, channel, routePolicy, int(configuration.MTU), func() error {
		if _, err := fmt.Fprintln(options.Ready, capabilities.ReadinessLine()); err != nil {
			return fmt.Errorf("announce OpenConnect packet route readiness: %w", err)
		}
		return nil
	})
}

func openConnectConfiguration(environment func(string) string) (protocol.NetworkConfiguration, []string, error) {
	mtuText := environment("INTERNAL_IP4_MTU")
	if mtuText == "" {
		mtuText = environment("INTERNAL_IP6_MTU")
	}
	mtuValue, err := strconv.ParseUint(mtuText, 10, 16)
	if err != nil || mtuValue == 0 {
		return protocol.NetworkConfiguration{}, nil, errors.New("OpenConnect must supply a valid MTU")
	}
	configuration := protocol.NetworkConfiguration{InterfaceName: "gsnet0", MTU: uint16(mtuValue)}
	families := make([]string, 0, 2)
	if addressText := environment("INTERNAL_IP4_ADDRESS"); addressText != "" {
		prefix, prefixErr := openConnectIPv4Prefix(addressText, environment("INTERNAL_IP4_NETMASKLEN"), environment("INTERNAL_IP4_NETMASK"))
		if prefixErr != nil {
			return protocol.NetworkConfiguration{}, nil, prefixErr
		}
		configuration.IPv4Address = &prefix
		families = append(families, "ipv4")
	}
	if addressText := environment("INTERNAL_IP6_ADDRESS"); addressText != "" {
		prefix, prefixErr := openConnectIPv6Prefix(addressText, environment("INTERNAL_IP6_NETMASK"))
		if prefixErr != nil {
			return protocol.NetworkConfiguration{}, nil, prefixErr
		}
		configuration.IPv6Address = &prefix
		families = append(families, "ipv6")
	}
	if len(families) == 0 {
		return protocol.NetworkConfiguration{}, nil, errors.New("OpenConnect supplied no tunnel address")
	}
	minimumMTU := uint64(576)
	if configuration.IPv6Address != nil {
		minimumMTU = 1280
	}
	if mtuValue < minimumMTU {
		return protocol.NetworkConfiguration{}, nil, fmt.Errorf("OpenConnect MTU must be at least %d for the negotiated address families", minimumMTU)
	}
	dnsText := environment("INTERNAL_IP4_DNS") + " " + environment("INTERNAL_IP6_DNS")
	seenDNS := make(map[netip.Addr]struct{}, 4)
	for _, value := range strings.Fields(dnsText) {
		address, parseErr := netip.ParseAddr(value)
		if parseErr != nil || !address.IsValid() || address.Zone() != "" || address.IsUnspecified() || address.IsMulticast() {
			return protocol.NetworkConfiguration{}, nil, errors.New("OpenConnect supplied an invalid DNS address")
		}
		address = address.Unmap()
		if address.IsLoopback() || address.IsLinkLocalUnicast() ||
			(address.Is4() && configuration.IPv4Address == nil) || (address.Is6() && configuration.IPv6Address == nil) {
			return protocol.NetworkConfiguration{}, nil, errors.New("OpenConnect DNS family has no tunnel address")
		}
		if _, exists := seenDNS[address]; exists {
			continue
		}
		seenDNS[address] = struct{}{}
		configuration.DNSServers = append(configuration.DNSServers, address)
	}
	if len(configuration.DNSServers) < 1 || len(configuration.DNSServers) > 4 {
		return protocol.NetworkConfiguration{}, nil, errors.New("OpenConnect must supply between one and four DNS addresses")
	}
	return configuration, families, nil
}

func openConnectIPv4Prefix(addressText, prefixText, maskText string) (netip.Prefix, error) {
	address, err := netip.ParseAddr(addressText)
	if err != nil || !address.Is4() {
		return netip.Prefix{}, errors.New("OpenConnect supplied an invalid IPv4 tunnel address")
	}
	prefixLength, err := strconv.Atoi(prefixText)
	if err != nil {
		mask := net.ParseIP(maskText).To4()
		if mask == nil {
			return netip.Prefix{}, errors.New("OpenConnect supplied an invalid IPv4 tunnel netmask")
		}
		ones, bits := net.IPMask(mask).Size()
		if bits != 32 || ones < 1 {
			return netip.Prefix{}, errors.New("OpenConnect supplied an invalid IPv4 tunnel netmask")
		}
		prefixLength = ones
	}
	if prefixLength < 1 || prefixLength > 32 {
		return netip.Prefix{}, errors.New("OpenConnect supplied an invalid IPv4 tunnel prefix")
	}
	return netip.PrefixFrom(address, prefixLength), nil
}

func openConnectIPv6Prefix(addressText, maskText string) (netip.Prefix, error) {
	if prefix, err := netip.ParsePrefix(addressText); err == nil && prefix.Addr().Is6() && prefix.Bits() >= 1 {
		return prefix, nil
	}
	address, err := netip.ParseAddr(addressText)
	if err != nil || !address.Is6() {
		return netip.Prefix{}, errors.New("OpenConnect supplied an invalid IPv6 tunnel address")
	}
	if prefix, prefixErr := netip.ParsePrefix(maskText); prefixErr == nil && prefix.Addr().Is6() && prefix.Bits() >= 1 {
		return netip.PrefixFrom(address, prefix.Bits()), nil
	}
	maskAddress, maskErr := netip.ParseAddr(maskText)
	if maskErr != nil || !maskAddress.Is6() {
		return netip.Prefix{}, errors.New("OpenConnect supplied an invalid IPv6 tunnel netmask")
	}
	ones, bits := net.IPMask(maskAddress.AsSlice()).Size()
	if bits != 128 || ones < 1 {
		return netip.Prefix{}, errors.New("OpenConnect supplied an invalid IPv6 tunnel netmask")
	}
	return netip.PrefixFrom(address, ones), nil
}

func openVPNFile(text string) (*os.File, error) {
	value, err := strconv.ParseUint(text, 10, 31)
	if err != nil || value < 3 {
		return nil, errors.New("OpenConnect supplied an invalid VPNFD")
	}
	file := os.NewFile(uintptr(value), "openconnect-vpnfd")
	if file == nil {
		return nil, errors.New("OpenConnect VPNFD is unavailable")
	}
	information, err := file.Stat()
	if err != nil || information.Mode()&os.ModeSocket == 0 {
		_ = file.Close()
		return nil, errors.New("OpenConnect VPNFD is not a Unix packet socket")
	}
	return file, nil
}

func readKeyFile(path string) ([]byte, error) {
	descriptor, err := unix.Open(path, unix.O_RDONLY|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0)
	if err != nil {
		return nil, fmt.Errorf("open authentication key file: %w", err)
	}
	file := os.NewFile(uintptr(descriptor), "workspace-network-key")
	if file == nil {
		_ = unix.Close(descriptor)
		return nil, errors.New("open authentication key file: invalid descriptor")
	}
	defer file.Close()
	var status unix.Stat_t
	if err := unix.Fstat(descriptor, &status); err != nil {
		return nil, fmt.Errorf("inspect authentication key file: %w", err)
	}
	if status.Mode&unix.S_IFMT != unix.S_IFREG || status.Mode&0o077 != 0 || status.Uid != uint32(os.Geteuid()) {
		return nil, errors.New("authentication key file must be a regular owner-only file")
	}
	key, err := readAuthenticationKey(file)
	if err != nil {
		return nil, fmt.Errorf("read authentication key file: %w", err)
	}
	return key, nil
}

func pumpVPNFD(
	ctx context.Context,
	vpn io.ReadWriteCloser,
	channel *protocol.Channel,
	routePolicy openConnectRoutePolicy,
	maximumPacketSize int,
	announceReady func() error,
) error {
	// A blocking inherited datagram read may survive peer death on macOS even
	// after shutdown. The network poller makes Close/deadlines interrupt it.
	if file, ok := vpn.(*os.File); ok {
		connection, err := net.FileConn(file)
		if err != nil {
			return fmt.Errorf("register OpenConnect packet socket: %w", err)
		}
		_ = file.Close()
		vpn = connection
		defer connection.Close()
	}
	var directStream *routedDirectPacketStream
	var directPlane *dataPlane
	if routePolicy.usesDirectPath() {
		upstream, err := directproxy.New()
		if err != nil {
			return fmt.Errorf("start direct side of OpenConnect split route: %w", err)
		}
		directStream = newRoutedDirectPacketStream(channel, maximumPacketSize)
		directPlane, err = newDataPlane(directStream, uint32(maximumPacketSize), upstream)
		if err != nil {
			directStream.Close()
			return fmt.Errorf("start direct side of OpenConnect split route: %w", err)
		}
	}
	if announceReady != nil {
		if err := announceReady(); err != nil {
			if directPlane != nil {
				directPlane.Close()
			}
			return err
		}
	}

	errorsChannel := make(chan error, 2)
	var interruptOnce sync.Once
	interrupt := func() {
		interruptOnce.Do(func() {
			if directStream != nil {
				_ = directStream.Close()
			}
			interruptVPN(vpn)
			_ = channel.Interrupt()
		})
	}
	var workers sync.WaitGroup
	workers.Add(2)
	go func() {
		defer workers.Done()
		buffer := make([]byte, protocol.MaximumPayloadLength)
		for {
			count, err := vpn.Read(buffer)
			if err != nil {
				errorsChannel <- fmt.Errorf("read OpenConnect VPNFD: %w", err)
				return
			}
			if count == 0 {
				errorsChannel <- io.ErrNoProgress
				return
			}
			if err := channel.SendPacket(buffer[:count]); err != nil {
				errorsChannel <- fmt.Errorf("send OpenConnect packet to guest: %w", err)
				return
			}
		}
	}()
	go func() {
		defer workers.Done()
		for {
			packet, err := channel.ReceivePacket()
			if err != nil {
				errorsChannel <- fmt.Errorf("receive guest packet for OpenConnect: %w", err)
				return
			}
			useVPN, routeErr := routePolicy.useVPN(packet)
			if routeErr != nil {
				clearBytes(packet)
				errorsChannel <- fmt.Errorf("route guest packet: %w", routeErr)
				return
			}
			if !useVPN && directStream != nil {
				if directErr := directStream.Deliver(packet); directErr != nil {
					clearBytes(packet)
					errorsChannel <- fmt.Errorf("send guest packet to direct split route: %w", directErr)
					return
				}
				continue
			}
			count, writeErr := writeVPNPacket(ctx, vpn, packet)
			packetLength := len(packet)
			clearBytes(packet)
			if writeErr != nil {
				errorsChannel <- fmt.Errorf("write OpenConnect VPNFD: %w", writeErr)
				return
			}
			if count != packetLength {
				errorsChannel <- io.ErrShortWrite
				return
			}
		}
	}()
	var err error
	if directPlane == nil {
		select {
		case err = <-errorsChannel:
		case <-ctx.Done():
			err = ctx.Err()
		}
	} else {
		select {
		case err = <-errorsChannel:
		case <-directPlane.done:
			err = errors.New("direct side of OpenConnect split route stopped")
		case <-ctx.Done():
			err = ctx.Err()
		}
	}
	interrupt()
	workers.Wait()
	if directPlane != nil {
		directPlane.Close()
	}
	if ctx.Err() != nil {
		return ctx.Err()
	}
	return err
}

func writeVPNPacket(ctx context.Context, vpn io.Writer, packet []byte) (int, error) {
	for {
		if err := ctx.Err(); err != nil {
			return 0, err
		}
		count, err := vpn.Write(packet)
		if count != 0 || !(errors.Is(err, unix.ENOBUFS) || errors.Is(err, unix.EAGAIN)) {
			return count, err
		}
		// Darwin can return ENOBUFS even for a blocking Unix datagram socket
		// while OpenConnect drains its receive queue. Keep this one packet and
		// apply backpressure instead of terminating the whole workspace route.
		// Waiting avoids a writable-poll busy loop when kernel buffers are full.
		timer := time.NewTimer(5 * time.Millisecond)
		select {
		case <-ctx.Done():
			timer.Stop()
			return 0, ctx.Err()
		case <-timer.C:
		}
	}
}

type routedDirectPacketStream struct {
	channel           *protocol.Channel
	maximumPacketSize int
	packets           chan []byte
	done              chan struct{}
	echo              *echoForwarder
	closeOnce         sync.Once
}

func newRoutedDirectPacketStream(channel *protocol.Channel, maximumPacketSize int) *routedDirectPacketStream {
	return &routedDirectPacketStream{
		channel:           channel,
		maximumPacketSize: maximumPacketSize,
		packets:           make(chan []byte),
		done:              make(chan struct{}),
		echo:              newEchoForwarder(channel, maximumPacketSize),
	}
}

// Deliver transfers ownership of packet to the direct stream on success.
func (stream *routedDirectPacketStream) Deliver(packet []byte) error {
	select {
	case stream.packets <- packet:
		return nil
	case <-stream.done:
		return io.ErrClosedPipe
	}
}

func (stream *routedDirectPacketStream) Read(buffer []byte) (int, error) {
	for {
		var packet []byte
		select {
		case packet = <-stream.packets:
		case <-stream.done:
			return 0, io.ErrClosedPipe
		}
		if stream.echo.Forward(packet) {
			clearBytes(packet)
			continue
		}
		if len(packet) > stream.maximumPacketSize || len(packet) > len(buffer) {
			clearBytes(packet)
			return 0, io.ErrShortBuffer
		}
		count := copy(buffer, packet)
		clearBytes(packet)
		return count, nil
	}
}

func (stream *routedDirectPacketStream) Write(packet []byte) (int, error) {
	if len(packet) > stream.maximumPacketSize {
		return 0, io.ErrShortBuffer
	}
	if err := stream.channel.SendPacket(packet); err != nil {
		return 0, err
	}
	return len(packet), nil
}

func (stream *routedDirectPacketStream) Close() error {
	stream.closeOnce.Do(func() {
		close(stream.done)
		stream.echo.Close()
	})
	return nil
}

func interruptVPN(vpn io.ReadWriteCloser) {
	if descriptor, ok := vpn.(interface{ Fd() uintptr }); ok {
		_ = unix.Shutdown(int(descriptor.Fd()), unix.SHUT_RDWR)
	}
	if deadline, ok := vpn.(interface{ SetDeadline(time.Time) error }); ok {
		_ = deadline.SetDeadline(time.Now())
	}
	_ = vpn.Close()
}
