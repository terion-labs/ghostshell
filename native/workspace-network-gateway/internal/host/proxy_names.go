package host

import (
	"context"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"net/netip"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
	socks "github.com/xjasonlyu/tun2socks/v2/transport/socks5"
	"golang.org/x/net/dns/dnsmessage"
)

var (
	proxyNamesIPv4    = netip.MustParsePrefix("198.18.0.0/16")
	proxyNamesIPv6    = netip.MustParsePrefix("fd00:4753:444e::/112")
	proxyLoopbackIPv4 = netip.MustParsePrefix("240.0.0.0/8")
	proxyLoopbackIPv6 = netip.MustParseAddr("fd00:4753:4c4f::1")
)

// proxyDNSNames belongs to exactly one service VM and route generation. Names
// are never resolved on the Mac or a public resolver: SOCKS CONNECT carries the
// original name to the selected SSH/workspace route. Never recycle entries;
// otherwise cached guest answers could silently acquire a different authority.
// The owner must stop the service VM if this gateway exits.
type proxyDNSNames struct {
	upstream    string
	credentials *proxyNameCredentials
	mu          sync.Mutex
	ids         map[string]uint16
	names       []string
}

type proxyNameCredentials struct{ username, password string }

// Service mode carries optional SOCKS credentials after the packet key over
// private stdin. Both lengths are single bytes, bounded by SOCKS5 itself.
func readServiceAuthentication(input io.Reader) ([]byte, *proxyNameCredentials, error) {
	key := make([]byte, 32)
	if _, err := io.ReadFull(input, key); err != nil {
		clearBytes(key)
		return nil, nil, err
	}
	fail := func(err error) ([]byte, *proxyNameCredentials, error) { clearBytes(key); return nil, nil, err }
	readText := func() (string, error) {
		var length [1]byte
		if _, err := io.ReadFull(input, length[:]); err != nil {
			return "", err
		}
		text := make([]byte, int(length[0]))
		defer clearBytes(text)
		if _, err := io.ReadFull(input, text); err != nil {
			return "", err
		}
		if !utf8.Valid(text) {
			return "", errors.New("invalid service proxy credential encoding")
		}
		return string(text), nil
	}
	username, err := readText()
	if err != nil {
		return fail(err)
	}
	password, err := readText()
	if err != nil {
		return fail(err)
	}
	var extra [1]byte
	if count, err := input.Read(extra[:]); count != 0 || !errors.Is(err, io.EOF) || (username == "") != (password == "") {
		return fail(errors.New("invalid service authentication frame"))
	}
	if username == "" {
		return key, nil, nil
	}
	return key, &proxyNameCredentials{username, password}, nil
}

func newProxyDNSNames(upstream string) *proxyDNSNames {
	return &proxyDNSNames{upstream: upstream, ids: make(map[string]uint16)}
}

func (value *proxyDNSNames) answer(query []byte) ([]byte, error) {
	var request dnsmessage.Message
	if err := request.Unpack(query); err != nil || request.Header.Response || request.Header.OpCode != 0 || len(request.Questions) != 1 || len(request.Answers) != 0 || len(request.Authorities) != 0 {
		return nil, errors.New("invalid service DNS query")
	}
	question := request.Questions[0]
	response := dnsmessage.Message{Header: dnsmessage.Header{ID: request.ID, Response: true, RecursionDesired: request.RecursionDesired, RecursionAvailable: true}, Questions: request.Questions}
	if question.Class != dnsmessage.ClassINET || (question.Type != dnsmessage.TypeA && question.Type != dnsmessage.TypeAAAA) {
		// SOCKS has no SRV/TXT lookup facility. Explicitly refuse unsupported
		// discovery instead of leaking it to the host's resolver.
		response.RCode = dnsmessage.RCodeNotImplemented
		return response.Pack()
	}
	name := strings.TrimSuffix(strings.ToLower(question.Name.String()), ".")
	if !validProxyDNSName(name) {
		response.RCode = dnsmessage.RCodeFormatError
		return response.Pack()
	}
	value.mu.Lock()
	id, exists := value.ids[name]
	if !exists && len(value.names) < 65534 {
		id = uint16(len(value.names) + 1)
		value.ids[name] = id
		value.names = append(value.names, name)
	}
	value.mu.Unlock()
	if id == 0 {
		response.RCode = dnsmessage.RCodeServerFailure
		return response.Pack()
	}
	header := dnsmessage.ResourceHeader{Name: question.Name, Class: dnsmessage.ClassINET, TTL: 60}
	if question.Type == dnsmessage.TypeA {
		response.Answers = []dnsmessage.Resource{{Header: header, Body: &dnsmessage.AResource{A: [4]byte{198, 18, byte(id >> 8), byte(id)}}}}
	} else {
		address := proxyNamesIPv6.Addr().As16()
		binary.BigEndian.PutUint16(address[14:], id)
		response.Answers = []dnsmessage.Resource{{Header: header, Body: &dnsmessage.AAAAResource{AAAA: address}}}
	}
	return response.Pack()
}

func validProxyDNSName(name string) bool {
	if len(name) == 0 || len(name) > 253 {
		return false
	}
	for _, label := range strings.Split(name, ".") {
		if len(label) == 0 || len(label) > 63 {
			return false
		}
		for _, character := range label {
			if !(character >= 'a' && character <= 'z' || character >= '0' && character <= '9' || character == '-' || character == '_') {
				return false
			}
		}
	}
	return true
}

// Loopback aliases are installed only in the dedicated service VM's OUTPUT
// NAT table. The driver keeps its original hostname/TLS identity; nothing is
// rewritten in connection strings and a localhost redirect stays on its route.
func (value *proxyDNSNames) destination(address netip.Addr, port uint16) (socks.Addr, error) {
	address = address.Unmap()
	var id uint16
	switch {
	case proxyNamesIPv4.Contains(address):
		bytes := address.As4()
		id = binary.BigEndian.Uint16(bytes[2:])
	case proxyNamesIPv6.Contains(address):
		bytes := address.As16()
		id = binary.BigEndian.Uint16(bytes[14:])
	case proxyLoopbackIPv4.Contains(address):
		bytes := address.As4()
		bytes[0] = 127
		return socks.SerializeAddr("", netip.AddrFrom4(bytes), port), nil
	case address == proxyLoopbackIPv6:
		return socks.SerializeAddr("", netip.IPv6Loopback(), port), nil
	default:
		return socks.SerializeAddr("", address, port), nil
	}
	value.mu.Lock()
	defer value.mu.Unlock()
	if id == 0 || int(id) > len(value.names) {
		return nil, errors.New("unknown service DNS destination")
	}
	return socks.SerializeAddr(value.names[int(id)-1], netip.Addr{}, port), nil
}

func (value *proxyDNSNames) dial(ctx context.Context, metadata *M.Metadata) (net.Conn, error) {
	destination, err := value.destination(metadata.DstIP, metadata.DstPort)
	if err != nil {
		return nil, err
	}
	connection, err := (&net.Dialer{Timeout: connectTimeout}).DialContext(ctx, "tcp", value.upstream)
	if err != nil {
		return nil, err
	}
	stop := context.AfterFunc(ctx, func() { connection.Close() })
	defer stop()
	var user *socks.User
	if value.credentials != nil {
		user = &socks.User{Username: value.credentials.username, Password: value.credentials.password}
	}
	if err = connection.SetDeadline(time.Now().Add(connectTimeout)); err == nil {
		_, err = socks.ClientHandshake(connection, destination, socks.CmdConnect, user)
	}
	if err == nil {
		err = connection.SetDeadline(time.Time{})
	}
	if err != nil {
		connection.Close()
		return nil, err
	}
	return connection, nil
}
