package host

import (
	"context"
	"encoding/base64"
	"errors"
	"net"
	"net/netip"
	"strings"
	"testing"
	"time"
)

func testWireGuardConfig() string {
	key := base64.StdEncoding.EncodeToString(make([]byte, 32))
	return "[Interface]\nAddress=10.2.0.2/32\nPrivateKey=" + key + "\nDNS=10.2.0.1\n[Peer]\nAllowedIPs=0.0.0.0/0\nEndpoint=127.0.0.1:51820\nPublicKey=" + key + "\n"
}

func TestWireGuardUnorderedFieldsAndPrivateDiagnostics(t *testing.T) {
	config, err := parseWireGuardConfig(context.Background(), []byte(testWireGuardConfig()))
	if err != nil {
		t.Fatal(err)
	}
	if config.network.IPv4Address.String() != "10.2.0.2/32" || config.network.DNSServers[0].String() != "10.2.0.1" {
		t.Fatal("network configuration lost")
	}
	if strings.Index(config.ipc, "public_key=") > strings.Index(config.ipc, "allowed_ip=") {
		t.Fatal("UAPI peer ordering invalid")
	}
	for _, malformed := range []string{
		testWireGuardConfig() + "\nsecret-as-unknown-key=value",
		strings.Replace(testWireGuardConfig(), "[Peer]", "[Interface]", 1),
		testWireGuardConfig() + "\nPublicKey=secret",
		strings.Replace(testWireGuardConfig(), "Address=10.2.0.2/32", "PostUp=secret", 1),
	} {
		_, err := parseWireGuardConfig(context.Background(), []byte(malformed))
		if err == nil || strings.Contains(err.Error(), "secret") {
			t.Fatalf("expected secret-safe failure, got %v", err)
		}
	}
}

func TestMemoryTUNOwnsPacketsAndClosesBlockedReaders(t *testing.T) {
	tun := newMemoryTUN(1280)
	transport := memoryPacketTransport{tun}
	packet := []byte{1, 2, 3}
	if _, err := transport.Write(packet); err != nil {
		t.Fatal(err)
	}
	packet[0] = 9
	buffers := [][]byte{make([]byte, 32)}
	sizes := make([]int, 1)
	if n, err := tun.Read(buffers, sizes, 4); err != nil || n != 1 || sizes[0] != 3 || buffers[0][4] != 1 {
		t.Fatalf("packet ownership/offset: %d %v", n, err)
	}
	if _, err := tun.Write([][]byte{{0, 4, 5}}, 1); err != nil {
		t.Fatal(err)
	}
	buffer := make([]byte, 32)
	if n, err := transport.Read(buffer); err != nil || n != 2 || buffer[0] != 4 {
		t.Fatalf("reverse packet: %d %v", n, err)
	}
	done := make(chan error, 1)
	go func() { _, err := transport.Read(buffer); done <- err }()
	_ = tun.Close()
	select {
	case err := <-done:
		if err == nil {
			t.Fatal("closed read succeeded")
		}
	case <-time.After(time.Second):
		t.Fatal("read did not unblock")
	}
}

func TestVPNDialTriesRemainingDNSAnswers(t *testing.T) {
	addresses := []netip.Addr{netip.MustParseAddr("192.0.2.1"), netip.MustParseAddr("192.0.2.2")}
	var attempts []netip.AddrPort
	client, server := net.Pipe()
	defer server.Close()
	result, err := dialVPNAddresses(context.Background(), addresses, 443, func(ctx context.Context, address netip.AddrPort) (net.Conn, error) {
		attempts = append(attempts, address)
		if len(attempts) == 1 {
			return nil, errors.New("first route unavailable")
		}
		return client, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	defer result.Close()
	if len(attempts) != 2 || attempts[1].Addr() != addresses[1] {
		t.Fatal("remaining DNS address was not tried")
	}
}
