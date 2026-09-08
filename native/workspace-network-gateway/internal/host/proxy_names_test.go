package host

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"net/netip"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
	socks "github.com/xjasonlyu/tun2socks/v2/transport/socks5"
	"golang.org/x/net/dns/dnsmessage"
)

func TestServiceModeOwnsItsDNSWithoutExternalResolverConfiguration(t *testing.T) {
	err := Run(context.Background(), Options{
		SocketPath: filepath.Join(t.TempDir(), "not-started.sock"),
		Mode:       "socks5", UpstreamHost: "127.0.0.1", UpstreamPort: 1,
		MTU: 1500, ResolveProxyNames: true,
		KeyInput: bytes.NewReader(make([]byte, 34)), Ready: io.Discard,
	})
	if err == nil || !strings.Contains(err.Error(), "connect to guest packet socket") {
		t.Fatalf("service mode should reach its private packet channel without --dns: %v", err)
	}
}

func serviceDNSQuery(t *testing.T, name string, kind dnsmessage.Type) []byte {
	t.Helper()
	question, err := dnsmessage.NewName(name)
	if err != nil {
		t.Fatal(err)
	}
	packet, err := (&dnsmessage.Message{Header: dnsmessage.Header{ID: 42, RecursionDesired: true}, Questions: []dnsmessage.Question{{Name: question, Type: kind, Class: dnsmessage.ClassINET}}}).Pack()
	if err != nil {
		t.Fatal(err)
	}
	return packet
}

func serviceDNSAddress(t *testing.T, resolver *proxyDNSNames, name string, kind dnsmessage.Type) netip.Addr {
	t.Helper()
	packet, err := resolver.answer(serviceDNSQuery(t, name, kind))
	if err != nil {
		t.Fatal(err)
	}
	var response dnsmessage.Message
	if err := response.Unpack(packet); err != nil {
		t.Fatal(err)
	}
	if response.ID != 42 || !response.Response || response.RCode != dnsmessage.RCodeSuccess || len(response.Answers) != 1 {
		t.Fatalf("invalid response: %+v", response)
	}
	switch body := response.Answers[0].Body.(type) {
	case *dnsmessage.AResource:
		return netip.AddrFrom4(body.A)
	case *dnsmessage.AAAAResource:
		return netip.AddrFrom16(body.AAAA)
	default:
		t.Fatal("unexpected DNS answer")
		return netip.Addr{}
	}
}

func TestProxyNamesPreserveRemotePrivateNameForBothAddressFamilies(t *testing.T) {
	resolver := newProxyDNSNames("unused")
	for _, kind := range []dnsmessage.Type{dnsmessage.TypeA, dnsmessage.TypeAAAA} {
		address := serviceDNSAddress(t, resolver, "PRIVATE.database.invalid.", kind)
		translated, err := resolver.destination(address, 1433)
		if err != nil || translated.String() != "private.database.invalid:1433" || translated[0] != socks.AtypDomainName {
			t.Fatalf("lost remote name: %v %v", translated, err)
		}
		if same := serviceDNSAddress(t, resolver, "private.database.invalid.", kind); same != address {
			t.Fatal("name allocation is not stable")
		}
	}
	if len(resolver.names) != 1 {
		t.Fatal("case/address-family duplicates consumed mapping slots")
	}
}

func TestProxyNamesReserveLoopbackAliasesWithoutChangingLogicalOrigin(t *testing.T) {
	resolver := newProxyDNSNames("unused")
	for source, target := range map[string]string{
		"240.0.0.1": "127.0.0.1:1433", "240.1.2.3": "127.1.2.3:1433",
		"fd00:4753:4c4f::1": "[::1]:1433", "192.0.2.10": "192.0.2.10:1433", "2001:db8::5": "[2001:db8::5]:1433",
	} {
		address, err := resolver.destination(netip.MustParseAddr(source), 1433)
		if err != nil || address.String() != target {
			t.Fatalf("%s: %v %v", source, address, err)
		}
	}
	for _, unknown := range []string{"198.18.0.0", "198.18.0.1", "198.18.255.255", "fd00:4753:444e::1"} {
		if _, err := resolver.destination(netip.MustParseAddr(unknown), 80); err == nil {
			t.Fatalf("unknown synthetic IP escaped: %s", unknown)
		}
	}
}

func TestProxyNamesDoNotLeakUnsupportedDiscoveryOrRecycleFullMap(t *testing.T) {
	resolver := newProxyDNSNames("unused")
	for _, kind := range []dnsmessage.Type{dnsmessage.TypeSRV, dnsmessage.TypeTXT, dnsmessage.TypePTR} {
		packet, err := resolver.answer(serviceDNSQuery(t, "private.invalid.", kind))
		if err != nil {
			t.Fatal(err)
		}
		var response dnsmessage.Message
		if err := response.Unpack(packet); err != nil || response.RCode != dnsmessage.RCodeNotImplemented {
			t.Fatalf("unsupported query not refused: %v", err)
		}
	}
	resolver.names = make([]string, 65534)
	resolver.names[0] = "stable.invalid"
	resolver.ids["stable.invalid"] = 1
	packet, err := resolver.answer(serviceDNSQuery(t, "new.invalid.", dnsmessage.TypeA))
	if err != nil {
		t.Fatal(err)
	}
	var response dnsmessage.Message
	if err := response.Unpack(packet); err != nil || response.RCode != dnsmessage.RCodeServerFailure {
		t.Fatal("mapping exhaustion not closed")
	}
	address, err := resolver.destination(netip.MustParseAddr("198.18.0.1"), 443)
	if err != nil || address.String() != "stable.invalid:443" {
		t.Fatal("old allocation changed")
	}
	for _, bad := range [][]byte{{}, {1, 2, 3}, bytes.Repeat([]byte{255}, 65535)} {
		if _, err := resolver.answer(bad); err == nil {
			t.Fatal("malformed DNS accepted")
		}
	}
}

func TestServiceAuthenticationIsStrictAndNeverRequiresArguments(t *testing.T) {
	key := bytes.Repeat([]byte{42}, 32)
	for _, pair := range [][2]string{{"", ""}, {"user", "secret"}, {strings.Repeat("a", 255), strings.Repeat("b", 255)}} {
		frame := append(bytes.Clone(key), byte(len(pair[0])))
		frame = append(frame, pair[0]...)
		frame = append(frame, byte(len(pair[1])))
		frame = append(frame, pair[1]...)
		got, credentials, err := readServiceAuthentication(bytes.NewReader(frame))
		if err != nil || !bytes.Equal(got, key) {
			t.Fatalf("valid auth rejected: %v", err)
		}
		if pair[0] != "" && (credentials == nil || credentials.username != pair[0] || credentials.password != pair[1]) {
			t.Fatal("credentials not preserved")
		}
		for cut := 0; cut < len(frame); cut++ {
			if _, _, err := readServiceAuthentication(bytes.NewReader(frame[:cut])); err == nil {
				t.Fatalf("accepted truncation %d", cut)
			}
		}
		if _, _, err := readServiceAuthentication(bytes.NewReader(append(frame, 0))); err == nil {
			t.Fatal("trailing bytes accepted")
		}
	}
	for _, suffix := range [][]byte{{1, 'u', 0}, {0, 1, 'p'}, {1, 255, 1, 'p'}} {
		if _, _, err := readServiceAuthentication(bytes.NewReader(append(bytes.Clone(key), suffix...))); err == nil {
			t.Fatal("invalid credential accepted")
		}
	}
}

func TestProxyNamesSendDomainAndCredentialsToSelectedSOCKSWithoutLocalDNS(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	result := make(chan error, 1)
	go func() {
		connection, err := listener.Accept()
		if err != nil {
			result <- err
			return
		}
		defer connection.Close()
		connection.SetDeadline(time.Now().Add(3 * time.Second))
		var header [2]byte
		if _, err = io.ReadFull(connection, header[:]); err != nil {
			result <- err
			return
		}
		methods := make([]byte, header[1])
		_, err = io.ReadFull(connection, methods)
		if err != nil {
			result <- err
			return
		}
		connection.Write([]byte{5, 2})
		if _, err = io.ReadFull(connection, header[:]); err != nil {
			result <- err
			return
		}
		user := make([]byte, header[1])
		io.ReadFull(connection, user)
		var length [1]byte
		io.ReadFull(connection, length[:])
		password := make([]byte, length[0])
		io.ReadFull(connection, password)
		if string(user) != "route-user" || string(password) != "route-secret" {
			result <- errors.New("wrong route authority")
			return
		}
		connection.Write([]byte{1, 0})
		var command [3]byte
		io.ReadFull(connection, command[:])
		buffer := make([]byte, 260)
		destination, err := socks.ReadAddr(connection, buffer)
		if err != nil || command != [3]byte{5, 1, 0} || destination.String() != "never-resolve-on-host.invalid:1433" {
			result <- errors.New("wrong SOCKS destination")
			return
		}
		connection.Write([]byte{5, 0, 0, 1, 127, 0, 0, 1, 0, 1})
		connection.Write([]byte("ready"))
		result <- nil
	}()
	resolver := newProxyDNSNames(listener.Addr().String())
	resolver.credentials = &proxyNameCredentials{"route-user", "route-secret"}
	address := serviceDNSAddress(t, resolver, "never-resolve-on-host.invalid.", dnsmessage.TypeA)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	connection, err := resolver.dial(ctx, &M.Metadata{Network: M.TCP, DstIP: address, DstPort: 1433})
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	var payload [5]byte
	if _, err := io.ReadFull(connection, payload[:]); err != nil || string(payload[:]) != "ready" {
		t.Fatalf("relay failed: %v", err)
	}
	if err := <-result; err != nil {
		t.Fatal(err)
	}
}

func TestProxyNameAllocationIsConcurrentAndAddressIDsDoNotOverlap(t *testing.T) {
	resolver := newProxyDNSNames("unused")
	var group sync.WaitGroup
	for index := 0; index < 32; index++ {
		group.Add(1)
		go func() { defer group.Done(); serviceDNSAddress(t, resolver, "same.invalid.", dnsmessage.TypeA) }()
	}
	group.Wait()
	if len(resolver.names) != 1 {
		t.Fatal("racing allocations created aliases")
	}
	first := serviceDNSAddress(t, resolver, "first.invalid.", dnsmessage.TypeA).As4()
	second := serviceDNSAddress(t, resolver, "second.invalid.", dnsmessage.TypeA).As4()
	if binary.BigEndian.Uint16(first[2:]) == binary.BigEndian.Uint16(second[2:]) {
		t.Fatal("different names share address")
	}
}
