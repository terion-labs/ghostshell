package host

import (
	"bufio"
	"bytes"
	"context"
	"io"
	"net"
	"net/http"
	"net/netip"
	"os"
	"path/filepath"
	"testing"
	"time"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
)

func TestProxyGatewayCancellationInterruptsGuestHandshake(t *testing.T) {
	directory, err := os.MkdirTemp("/tmp", "gs-proxy-")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(directory)
	path := filepath.Join(directory, "guest.sock")
	listener, err := net.Listen("unix", path)
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	accepted := make(chan net.Conn, 1)
	go func() {
		connection, err := listener.Accept()
		if err == nil {
			accepted <- connection
		}
	}()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	result := make(chan error, 1)
	go func() {
		result <- Run(ctx, Options{Mode: "direct", SocketPath: path, MTU: 1280, DNSServers: []netip.Addr{netip.MustParseAddr("1.1.1.1")}, KeyInput: bytes.NewReader(make([]byte, 32)), Ready: io.Discard})
	}()
	select {
	case connection := <-accepted:
		defer connection.Close()
	case <-time.After(time.Second):
		t.Fatal("guest not dialed")
	}
	cancel()
	select {
	case err := <-result:
		if err == nil {
			t.Fatal("stalled guest was accepted")
		}
	case <-time.After(time.Second):
		t.Fatal("guest handshake ignored cancellation")
	}
}

func TestHTTPSProxyCancellationInterruptsConnectResponse(t *testing.T) {
	client, server := net.Pipe()
	defer server.Close()
	requestRead := make(chan struct{})
	go func() { _, _ = http.ReadRequest(bufio.NewReader(server)); close(requestRead) }()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	result := make(chan error, 1)
	go func() {
		_, err := connectHTTPSTunnel(ctx, client, &M.Metadata{Network: M.TCP, DstIP: netip.MustParseAddr("192.0.2.1"), DstPort: 443})
		result <- err
	}()
	<-requestRead
	cancel()
	select {
	case err := <-result:
		if err == nil {
			t.Fatal("missing CONNECT response accepted")
		}
	case <-time.After(time.Second):
		t.Fatal("CONNECT response ignored cancellation")
	}
}

func TestHTTPSProxyTunnelPreservesBufferedPayloadAndOutlivesDialContext(t *testing.T) {
	client, server := net.Pipe()
	defer server.Close()
	go func() {
		_, _ = http.ReadRequest(bufio.NewReader(server))
		_, _ = io.WriteString(server, "HTTP/1.1 200 Connection Established\r\n\r\nhello")
	}()
	ctx, cancel := context.WithCancel(context.Background())
	connection, err := connectHTTPSTunnel(ctx, client, &M.Metadata{Network: M.TCP, DstIP: netip.MustParseAddr("192.0.2.1"), DstPort: 443})
	cancel()
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	_ = connection.SetReadDeadline(time.Now().Add(time.Second))
	data := make([]byte, 5)
	if _, err := io.ReadFull(connection, data); err != nil || string(data) != "hello" {
		t.Fatalf("buffered tunnel data lost: %q %v", data, err)
	}
}
