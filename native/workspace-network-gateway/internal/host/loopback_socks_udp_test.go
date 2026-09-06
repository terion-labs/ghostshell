package host

import (
	"bytes"
	"encoding/binary"
	"io"
	"net"
	"net/netip"
	"testing"
	"time"

	M "github.com/xjasonlyu/tun2socks/v2/metadata"
	wire "github.com/xjasonlyu/tun2socks/v2/transport/socks5"
)

func socksUDPFixture(t *testing.T, relayIP net.IP) (string, <-chan net.Conn, <-chan []byte) {
	t.Helper()
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { listener.Close() })
	controls := make(chan net.Conn, 4)
	packets := make(chan []byte, 4)
	go func() {
		control, err := listener.Accept()
		if err != nil {
			return
		}
		defer control.Close()
		control.SetDeadline(time.Now().Add(5 * time.Second))
		var greeting [3]byte
		if _, err := io.ReadFull(control, greeting[:]); err != nil {
			return
		}
		if !bytes.Equal(greeting[:], []byte{5, 1, 0}) {
			return
		}
		if _, err := control.Write([]byte{5, 0}); err != nil {
			return
		}
		var request [10]byte
		if _, err := io.ReadFull(control, request[:]); err != nil {
			return
		}
		if request[1] != 3 {
			return
		}
		relay, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
		if err != nil {
			return
		}
		defer relay.Close()
		reply := []byte{5, 0, 0, 1, 0, 0, 0, 0, 0, 0}
		copy(reply[4:8], relayIP.To4())
		binary.BigEndian.PutUint16(reply[8:], uint16(relay.LocalAddr().(*net.UDPAddr).Port))
		if _, err := control.Write(reply); err != nil {
			return
		}
		controls <- control
		go func() { io.Copy(io.Discard, control); relay.Close() }()
		buffer := make([]byte, 65535)
		n, client, err := relay.ReadFromUDP(buffer)
		if err != nil {
			return
		}
		packets <- bytes.Clone(buffer[:n])
		// An unrelated UDP sender cannot impersonate the associated relay.
		intruder, err := net.DialUDP("udp4", nil, client)
		if err == nil {
			intruder.Write([]byte("spoof"))
			intruder.Close()
		}
		relay.WriteToUDP(buffer[:n], client)
		io.Copy(io.Discard, control)
	}()
	return listener.Addr().String(), controls, packets
}

func TestOwnedSOCKSUDPRelaysPacketsAndDiesWithItsControlSession(t *testing.T) {
	address, controls, packets := socksUDPFixture(t, net.IPv4(127, 0, 0, 1))
	upstream := &loopbackSOCKSUDP{address: address}
	route := newTCPDNSProxy(upstream, []netip.Addr{netip.MustParseAddr("100.100.100.100")}, false)
	route.allowUDP = true
	metadata := &M.Metadata{Network: M.UDP, DstIP: netip.MustParseAddr("192.0.2.123"), DstPort: 12345}
	connection, err := route.DialUDP(metadata)
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	control := <-controls
	connection.SetDeadline(time.Now().Add(2 * time.Second))
	payload := []byte("only through the selected relay")
	if n, err := connection.WriteTo(payload, metadata.UDPAddr()); err != nil || n != len(payload) {
		t.Fatalf("write: %d %v", n, err)
	}
	buffer := make([]byte, 512)
	n, remote, err := connection.ReadFrom(buffer)
	if err != nil || !bytes.Equal(buffer[:n], payload) || remote.String() != "192.0.2.123:12345" {
		t.Fatalf("read: %q %v %v", buffer[:n], remote, err)
	}
	encoded := <-packets
	target, body, err := wire.DecodeUDPPacket(encoded)
	if err != nil || target.UDPAddr().String() != remote.String() || !bytes.Equal(body, payload) {
		t.Fatalf("wire: %v", err)
	}
	control.Close()
	if _, _, err := connection.ReadFrom(buffer); err == nil {
		t.Fatal("UDP association survived control close")
	}
}

func TestUDPReadinessRequiresSuccessfulLoopbackAssociation(t *testing.T) {
	for _, test := range []struct {
		name    string
		relay   net.IP
		allowed bool
	}{
		{"owned relay", net.IPv4(127, 0, 0, 1), true},
		{"external relay", net.IPv4(192, 0, 2, 42), false},
		{"unspecified relay", net.IPv4zero, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			address, _, _ := socksUDPFixture(t, test.relay)
			endpoint := netip.MustParseAddrPort(address)
			_, capabilities, err := newUpstream(Options{Mode: "socks5", UpstreamHost: endpoint.Addr().String(), UpstreamPort: endpoint.Port(), MTU: 1280, AllowUDPAssociate: true})
			if test.allowed {
				if err != nil || capabilities.ReadinessLine() != "READY v1 families=ipv4,ipv6 protocols=tcp,udp mtu=1280" {
					t.Fatalf("readiness: %v %v", capabilities, err)
				}
			} else if err == nil {
				t.Fatal("untrusted relay was advertised")
			}
		})
	}
}

func TestUDPAssociationCannotResolveOrDialAnExternalControlEndpoint(t *testing.T) {
	for _, address := range []string{"example.test:1080", "192.0.2.42:1080"} {
		if _, err := (&loopbackSOCKSUDP{address: address}).DialUDP(nil); err == nil {
			t.Fatalf("accepted %s", address)
		}
	}
}
