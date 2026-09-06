package host

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"io"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"testing"
	"time"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
)

type dnsTestProxy struct {
	dial func(context.Context, *M.Metadata) (net.Conn, error)
}

func (value dnsTestProxy) DialContext(ctx context.Context, metadata *M.Metadata) (net.Conn, error) {
	return value.dial(ctx, metadata)
}
func (dnsTestProxy) DialUDP(*M.Metadata) (net.PacketConn, error) {
	return nil, errors.New("UDP unavailable")
}

func TestProxyDNSUsesTLSIdentityAndLiteralBootstrapInsideSelectedProxy(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), DNSNames: []string{"cloudflare-dns.com"}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	certificate, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatal(err)
	}
	query := []byte{0x12, 0x34, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0}
	response := bytes.Clone(query)
	response[2] |= 0x80
	requests := make(chan string, 2)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		requests <- request.TLS.ServerName
		body, _ := io.ReadAll(request.Body)
		if request.Method != http.MethodPost || !bytes.Equal(body, query) {
			writer.WriteHeader(400)
			return
		}
		writer.Header().Set("Content-Type", "application/dns-message")
		writer.Write(response)
	}))
	server.TLS = &tls.Config{Certificates: []tls.Certificate{{Certificate: [][]byte{der}, PrivateKey: key}}}
	server.StartTLS()
	defer server.Close()
	dials := make(chan M.Metadata, 2)
	upstream := dnsTestProxy{dial: func(ctx context.Context, metadata *M.Metadata) (net.Conn, error) {
		dials <- *metadata
		return (&net.Dialer{}).DialContext(ctx, "tcp", server.Listener.Addr().String())
	}}
	resolver := netip.MustParseAddr("1.1.1.1")
	value := newTCPDNSProxy(upstream, []netip.Addr{resolver}, true)
	defer value.doh.CloseIdleConnections()
	roots := x509.NewCertPool()
	roots.AddCert(certificate)
	value.doh.Transport.(*http.Transport).TLSClientConfig = &tls.Config{RootCAs: roots}
	metadata := &M.Metadata{Network: M.UDP, DstIP: resolver, DstPort: 53}
	connection, err := value.DialUDP(metadata)
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	connection.SetDeadline(time.Now().Add(3 * time.Second))
	if _, err := connection.WriteTo(query, metadata.UDPAddr()); err != nil {
		t.Fatal(err)
	}
	buffer := make([]byte, 512)
	n, address, err := connection.ReadFrom(buffer)
	if err != nil || !bytes.Equal(buffer[:n], response) || address.String() != "1.1.1.1:53" {
		t.Fatalf("DNS response: %v %v", address, err)
	}
	if got := <-requests; got != "cloudflare-dns.com" {
		t.Fatalf("TLS identity = %q", got)
	}
	if got := <-dials; got.DestinationAddress() != "1.1.1.1:443" {
		t.Fatalf("bootstrap = %v", got)
	}
	if _, err := value.DialUDP(&M.Metadata{Network: M.UDP, DstIP: resolver, DstPort: 443}); err == nil {
		t.Fatal("arbitrary UDP was accepted")
	}
	if _, err := connection.WriteTo(query, &net.UDPAddr{IP: net.IPv4(8, 8, 8, 8), Port: 53}); err == nil {
		t.Fatal("undeclared DNS was accepted")
	}
	// Removing trust must fail, not disable verification or switch to a direct resolver.
	value.doh.CloseIdleConnections()
	value.doh.Transport.(*http.Transport).TLSClientConfig = &tls.Config{RootCAs: x509.NewCertPool()}
	if _, err := value.exchange(context.Background(), metadata, query); err == nil {
		t.Fatal("untrusted DoH certificate accepted")
	}
}

func TestTailscaleDNSUsesConfiguredResolverTCPAndPreservesFraming(t *testing.T) {
	query := []byte{1, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0}
	dials := make(chan M.Metadata, 1)
	upstream := dnsTestProxy{dial: func(_ context.Context, metadata *M.Metadata) (net.Conn, error) {
		dials <- *metadata
		client, server := net.Pipe()
		go func() {
			defer server.Close()
			message, err := readDNSFrame(server)
			if err != nil {
				return
			}
			message[2] |= 0x80
			writeDNSFrame(server, message)
		}()
		return client, nil
	}}
	resolver := netip.MustParseAddr("100.100.100.100")
	value := newTCPDNSProxy(upstream, []netip.Addr{resolver}, false)
	value.allowUDP = true // DNS must still use TCP even when arbitrary UDP is supported.
	metadata := &M.Metadata{Network: M.UDP, DstIP: resolver, DstPort: 53}
	connection, err := value.DialUDP(metadata)
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	connection.SetDeadline(time.Now().Add(time.Second))
	if _, err := connection.WriteTo(query, metadata.UDPAddr()); err != nil {
		t.Fatal(err)
	}
	buffer := make([]byte, 512)
	if n, _, err := connection.ReadFrom(buffer); err != nil || n != 12 || buffer[2]&0x80 == 0 {
		t.Fatalf("DNS response: %d %v", n, err)
	}
	if got := <-dials; got.Network != M.TCP || got.DestinationAddress() != "100.100.100.100:53" {
		t.Fatalf("upstream DNS: %v", got)
	}
}

func TestGenericSOCKSRouteDoesNotAdvertiseUnimplementedUDP(t *testing.T) {
	_, capabilities, err := newUpstream(Options{Mode: "socks5", UpstreamHost: "127.0.0.1", UpstreamPort: 1080, MTU: 1280})
	if err != nil {
		t.Fatal(err)
	}
	if capabilities.ReadinessLine() != "READY v1 families=ipv4,ipv6 protocols=tcp mtu=1280" {
		t.Fatal(capabilities)
	}
}
