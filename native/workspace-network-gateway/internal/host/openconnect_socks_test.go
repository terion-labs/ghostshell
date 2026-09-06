package host

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"strconv"
	"testing"
	"time"

	"golang.org/x/net/dns/dnsmessage"
	"golang.org/x/net/proxy"
	"golang.org/x/sys/unix"
	"gvisor.dev/gvisor/pkg/tcpip/adapters/gonet"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

func TestOpenConnectSOCKSRoutesVPNDNSAndSplitTraffic(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	fds, err := unix.Socketpair(unix.AF_UNIX, unix.SOCK_DGRAM, 0)
	if err != nil {
		t.Fatal(err)
	}
	peer, err := newVPNSocketStack(ctx, os.NewFile(uintptr(fds[1]), "test-vpn-peer"), protocol.NetworkConfiguration{
		MTU: 1400, IPv4Address: prefixPointer(netip.MustParsePrefix("10.20.30.1/24")),
	})
	if err != nil {
		t.Fatal(err)
	}
	defer peer.Close()
	localDNS := vpnSocketAddress(netip.MustParseAddrPort("10.20.30.1:53"))
	dns, err := gonet.DialUDP(peer.stack, &localDNS, nil, ipv4.ProtocolNumber)
	if err != nil {
		t.Fatal(err)
	}
	defer dns.Close()
	dnsDone := make(chan struct{})
	go func() {
		defer close(dnsDone)
		packet := make([]byte, 4096)
		for {
			n, client, err := dns.ReadFrom(packet)
			if err != nil {
				return
			}
			var query dnsmessage.Message
			if query.Unpack(packet[:n]) != nil || len(query.Questions) != 1 {
				return
			}
			question := query.Questions[0]
			ip := [4]byte{10, 20, 30, 1}
			if question.Name.String() == "public.test." {
				ip = [4]byte{127, 0, 0, 1}
			}
			answer := dnsmessage.Message{Header: dnsmessage.Header{ID: query.ID, Response: true}, Questions: query.Questions,
				Answers: []dnsmessage.Resource{{Header: dnsmessage.ResourceHeader{Name: question.Name, Type: dnsmessage.TypeA, Class: dnsmessage.ClassINET, TTL: 30}, Body: &dnsmessage.AResource{A: ip}}}}
			response, err := answer.Pack()
			if err != nil {
				return
			}
			if _, err := dns.WriteTo(response, client); err != nil {
				return
			}
		}
	}()
	vpnService, err := gonet.ListenTCP(peer.stack, vpnSocketAddress(netip.MustParseAddrPort("10.20.30.1:8443")), ipv4.ProtocolNumber)
	if err != nil {
		t.Fatal(err)
	}
	defer vpnService.Close()
	directService, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer directService.Close()
	for _, listener := range []net.Listener{vpnService, directService} {
		go func() {
			client, err := listener.Accept()
			if err != nil {
				return
			}
			defer client.Close()
			_ = client.SetDeadline(time.Now().Add(5 * time.Second))
			_, _ = io.Copy(client, client)
		}()
	}
	reservation, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	port := reservation.Addr().(*net.TCPAddr).Port
	_ = reservation.Close()
	env := environmentValues(map[string]string{
		"VPNFD": strconv.Itoa(fds[0]), "INTERNAL_IP4_ADDRESS": "10.20.30.2",
		"INTERNAL_IP4_NETMASKLEN": "24", "INTERNAL_IP4_MTU": "1400", "INTERNAL_IP4_DNS": "10.20.30.1",
		"CISCO_SPLIT_INC": "1", "CISCO_SPLIT_INC_0_ADDR": "10.0.0.0", "CISCO_SPLIT_INC_0_MASKLEN": "8",
	})
	readyReader, readyWriter := io.Pipe()
	defer readyReader.Close()
	defer readyWriter.Close()
	result := make(chan error, 1)
	go func() { result <- RunOpenConnectSOCKS(ctx, uint16(port), env, readyWriter) }()
	ready := make(chan error, 1)
	go func() { var line [128]byte; _, err := readyReader.Read(line[:]); ready <- err }()
	select {
	case err := <-ready:
		if err != nil {
			t.Fatal(err)
		}
	case err := <-result:
		t.Fatalf("startup: %v", err)
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	dialer, err := proxy.SOCKS5("tcp", fmt.Sprintf("127.0.0.1:%d", port), nil, &net.Dialer{Timeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	for _, target := range []string{"private.test:8443", fmt.Sprintf("public.test:%d", directService.Addr().(*net.TCPAddr).Port)} {
		conn, err := dialer.(proxy.ContextDialer).DialContext(ctx, "tcp", target)
		if err != nil {
			t.Fatalf("connect %s: %v", target, err)
		}
		_ = conn.SetDeadline(time.Now().Add(time.Second))
		_, err = conn.Write([]byte("ping"))
		if err != nil {
			t.Fatal(err)
		}
		var reply [4]byte
		_, err = io.ReadFull(conn, reply[:])
		_ = conn.Close()
		if err != nil || string(reply[:]) != "ping" {
			t.Fatalf("echo %s: %q, %v", target, reply, err)
		}
	}
	// Losing VPNFD also removes split-direct access. A healthy listener alone
	// must never outlive the VPN and silently keep serving external traffic.
	peer.Close()
	// Unix datagram sockets report a lost peer on the next send, not EOF.
	go func() {
		conn, err := dialer.(proxy.ContextDialer).DialContext(ctx, "tcp", "10.20.30.1:8443")
		if err == nil {
			_ = conn.Close()
		}
	}()
	select {
	case err := <-result:
		if err == nil {
			t.Fatal("expected packet channel failure")
		}
	case <-ctx.Done():
		t.Fatal("SOCKS listener survived loss of VPNFD")
	}
	if conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", port), time.Second); err == nil {
		_ = conn.Close()
		t.Fatal("listener remained open after VPN failure")
	}
	_ = dns.Close()
	<-dnsDone
}

func TestHostSOCKSRouteChoiceMatchesGuestPolicy(t *testing.T) {
	for _, full := range []bool{false, true} {
		values := map[string]string{}
		if !full {
			values = map[string]string{"CISCO_SPLIT_INC": "1", "CISCO_SPLIT_INC_0_ADDR": "10.0.0.0", "CISCO_SPLIT_INC_0_MASKLEN": "8"}
		}
		policy, err := parseOpenConnectRoutePolicy(environmentValues(values), protocol.NetworkConfiguration{
			IPv4Address: prefixPointer(netip.MustParsePrefix("10.0.0.2/24")), DNSServers: []netip.Addr{netip.MustParseAddr("192.0.2.53")},
		})
		if err != nil {
			t.Fatal(err)
		}
		for _, test := range []struct {
			ip   string
			want bool
		}{{"10.2.3.4", true}, {"192.0.2.53", true}, {"1.1.1.1", full}} {
			if got := policy.useVPNAddress(netip.MustParseAddr(test.ip)); got != test.want {
				t.Fatalf("full=%v ip=%s: got %v", full, test.ip, got)
			}
		}
	}
}
