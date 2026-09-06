package host

import (
	"context"
	"errors"
	"io"
	"net"
	"net/netip"
	"sync"
	"time"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
	"github.com/xjasonlyu/tun2socks/v2/proxy"
	wire "github.com/xjasonlyu/tun2socks/v2/transport/socks5"
)

// loopbackSOCKSUDP is reserved for the owned Tailscale daemon. Both the control
// socket and advertised relay must stay on loopback; DNS names are not resolved.
type loopbackSOCKSUDP struct {
	proxy.Proxy
	address string
}

func (value *loopbackSOCKSUDP) DialUDP(*M.Metadata) (net.PacketConn, error) {
	endpoint, err := netip.ParseAddrPort(value.address)
	if err != nil || !endpoint.Addr().IsLoopback() || endpoint.Port() == 0 {
		return nil, errors.New("UDP association requires an owned loopback SOCKS endpoint")
	}
	ctx, cancel := context.WithTimeout(context.Background(), connectTimeout)
	defer cancel()
	control, err := (&net.Dialer{}).DialContext(ctx, "tcp", value.address)
	if err != nil {
		return nil, err
	}
	control.SetDeadline(time.Now().Add(connectTimeout))
	address, err := wire.ClientHandshake(control, wire.Addr{wire.AtypIPv4, 0, 0, 0, 0, 0, 0}, wire.CmdUDPAssociate, nil)
	if err != nil {
		control.Close()
		return nil, err
	}
	relay := address.UDPAddr()
	if relay == nil || !relay.IP.IsLoopback() || relay.Port == 0 {
		control.Close()
		return nil, errors.New("SOCKS UDP relay must be a literal loopback address")
	}
	control.SetDeadline(time.Time{})
	// A connected UDP socket accepts packets only from this verified relay.
	connection, err := net.DialUDP("udp", nil, relay)
	if err != nil {
		control.Close()
		return nil, err
	}
	valueConnection := &loopbackSOCKSPacketConn{UDPConn: connection, control: control}
	go func() { io.Copy(io.Discard, control); valueConnection.Close() }()
	return valueConnection, nil
}

type loopbackSOCKSPacketConn struct {
	*net.UDPConn
	control net.Conn
	once    sync.Once
}

func (connection *loopbackSOCKSPacketConn) Close() error {
	connection.once.Do(func() { connection.control.Close(); connection.UDPConn.Close() })
	return nil
}

func (connection *loopbackSOCKSPacketConn) WriteTo(payload []byte, address net.Addr) (int, error) {
	target, err := netip.ParseAddrPort(address.String())
	if err != nil {
		return 0, errors.New("SOCKS UDP destination must be a literal IP")
	}
	packet, err := wire.EncodeUDPPacket(wire.ParseAddr(net.UDPAddrFromAddrPort(target)), payload)
	if err != nil {
		return 0, err
	}
	if _, err = connection.UDPConn.Write(packet); err != nil {
		return 0, err
	}
	return len(payload), nil
}

func (connection *loopbackSOCKSPacketConn) ReadFrom(buffer []byte) (int, net.Addr, error) {
	packet := make([]byte, 65535)
	n, err := connection.UDPConn.Read(packet)
	if err != nil {
		return 0, nil, err
	}
	if n < 3 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0 {
		return 0, nil, errors.New("invalid or fragmented SOCKS UDP packet")
	}
	address, payload, err := wire.DecodeUDPPacket(packet[:n])
	if err != nil {
		return 0, nil, err
	}
	remote := address.UDPAddr()
	if remote == nil {
		return 0, nil, errors.New("SOCKS UDP response must identify a literal IP")
	}
	return copy(buffer, payload), remote, nil
}
