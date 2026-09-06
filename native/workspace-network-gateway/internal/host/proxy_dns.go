package host

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"io"
	"mime"
	"net"
	"net/http"
	"net/netip"
	"sync"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
	"github.com/xjasonlyu/tun2socks/v2/proxy"
)

// tcpDNSProxy translates only the configured resolver's DNS traffic. It never
// turns a TCP-only proxy into an advertised general-purpose UDP route.
type tcpDNSProxy struct {
	proxy.Proxy
	servers  []netip.Addr
	doh      *http.Client
	allowUDP bool
}

func newTCPDNSProxy(upstream proxy.Proxy, servers []netip.Addr, doh bool) *tcpDNSProxy {
	value := &tcpDNSProxy{Proxy: upstream, servers: servers}
	if doh {
		value.doh = &http.Client{Timeout: connectTimeout, Transport: &http.Transport{
			// The URL supplies the TLS identity, but bootstrap is a literal IP
			// dialed inside the selected proxy. No host resolver or direct fallback.
			DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
				return upstream.DialContext(ctx, &M.Metadata{Network: M.TCP, DstIP: netip.MustParseAddr("1.1.1.1"), DstPort: 443})
			},
		}, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	}
	return value
}

func (value *tcpDNSProxy) isDNS(metadata *M.Metadata) bool {
	if metadata.DstPort != 53 {
		return false
	}
	for _, server := range value.servers {
		if server.Unmap() == metadata.DstIP.Unmap() {
			return true
		}
	}
	return false
}

func (value *tcpDNSProxy) DialContext(ctx context.Context, metadata *M.Metadata) (net.Conn, error) {
	if !value.isDNS(metadata) || value.doh == nil {
		return value.Proxy.DialContext(ctx, metadata)
	}
	return value.dnsPipe(metadata), nil
}

func (value *tcpDNSProxy) DialUDP(metadata *M.Metadata) (net.PacketConn, error) {
	if !value.isDNS(metadata) {
		if value.allowUDP {
			return value.Proxy.DialUDP(metadata)
		}
		return nil, errors.New("selected proxy supports TCP and DNS only")
	}
	return &dnsPacketConn{Conn: value.dnsPipe(metadata), address: net.UDPAddrFromAddrPort(metadata.DestinationAddrPort())}, nil
}

func (value *tcpDNSProxy) dnsPipe(metadata *M.Metadata) net.Conn {
	client, server := net.Pipe()
	ctx, cancel := context.WithCancel(context.Background())
	connection := &cancelConn{Conn: client, cancel: cancel}
	destination := *metadata
	go func() {
		defer server.Close()
		defer cancel()
		for {
			query, err := readDNSFrame(server)
			if err != nil {
				return
			}
			response, err := value.exchange(ctx, &destination, query)
			if err != nil {
				return
			}
			if err := writeDNSFrame(server, response); err != nil {
				return
			}
		}
	}()
	return connection
}

func (value *tcpDNSProxy) exchange(ctx context.Context, metadata *M.Metadata, query []byte) ([]byte, error) {
	ctx, cancel := context.WithTimeout(ctx, connectTimeout)
	defer cancel()
	if len(query) < 12 {
		return nil, errors.New("invalid DNS query")
	}
	var response []byte
	if value.doh != nil {
		request, err := http.NewRequestWithContext(ctx, http.MethodPost, "https://cloudflare-dns.com/dns-query", bytes.NewReader(query))
		if err != nil {
			return nil, err
		}
		request.Header.Set("Content-Type", "application/dns-message")
		request.Header.Set("Accept", "application/dns-message")
		result, err := value.doh.Do(request)
		if err != nil {
			return nil, err
		}
		defer result.Body.Close()
		if result.StatusCode != http.StatusOK {
			return nil, errors.New("routed DNS-over-HTTPS request failed")
		}
		contentType, _, err := mime.ParseMediaType(result.Header.Get("Content-Type"))
		if err != nil || contentType != "application/dns-message" {
			return nil, errors.New("routed DNS-over-HTTPS response is not a DNS message")
		}
		response, err = io.ReadAll(io.LimitReader(result.Body, 65536))
		if err != nil {
			return nil, err
		}
	} else {
		destination := *metadata
		destination.Network = M.TCP
		connection, err := value.Proxy.DialContext(ctx, &destination)
		if err != nil {
			return nil, err
		}
		defer connection.Close()
		stop := context.AfterFunc(ctx, func() { connection.Close() })
		defer stop()
		if err := writeDNSFrame(connection, query); err != nil {
			return nil, err
		}
		response, err = readDNSFrame(connection)
		if err != nil {
			return nil, err
		}
	}
	if len(response) < 12 || len(response) > 65535 || response[0] != query[0] || response[1] != query[1] || response[2]&0x80 == 0 {
		return nil, errors.New("invalid routed DNS response")
	}
	return response, nil
}

type cancelConn struct {
	net.Conn
	cancel context.CancelFunc
}

func (connection *cancelConn) Close() error { connection.cancel(); return connection.Conn.Close() }

type dnsPacketConn struct {
	net.Conn
	address *net.UDPAddr
	readMu  sync.Mutex
	writeMu sync.Mutex
}

func (connection *dnsPacketConn) ReadFrom(buffer []byte) (int, net.Addr, error) {
	connection.readMu.Lock()
	defer connection.readMu.Unlock()
	response, err := readDNSFrame(connection.Conn)
	if err != nil {
		return 0, nil, err
	}
	return copy(buffer, response), connection.address, nil
}

func (connection *dnsPacketConn) WriteTo(buffer []byte, destination net.Addr) (int, error) {
	if destination.String() != connection.address.String() {
		return 0, errors.New("DNS transport cannot forward other UDP destinations")
	}
	connection.writeMu.Lock()
	defer connection.writeMu.Unlock()
	if err := writeDNSFrame(connection.Conn, buffer); err != nil {
		return 0, err
	}
	return len(buffer), nil
}

func readDNSFrame(reader io.Reader) ([]byte, error) {
	var header [2]byte
	if _, err := io.ReadFull(reader, header[:]); err != nil {
		return nil, err
	}
	message := make([]byte, int(binary.BigEndian.Uint16(header[:])))
	_, err := io.ReadFull(reader, message)
	return message, err
}

func writeDNSFrame(writer io.Writer, message []byte) error {
	if len(message) < 12 || len(message) > 65535 {
		return errors.New("invalid DNS message length")
	}
	frame := make([]byte, 2+len(message))
	binary.BigEndian.PutUint16(frame, uint16(len(message)))
	copy(frame[2:], message)
	_, err := io.Copy(writer, bytes.NewReader(frame))
	return err
}
