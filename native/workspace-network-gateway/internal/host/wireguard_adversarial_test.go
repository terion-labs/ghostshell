package host

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	"golang.org/x/crypto/curve25519"
	"golang.zx2c4.com/wireguard/conn"
	"golang.zx2c4.com/wireguard/device"
)

func TestWireGuardRejectsUnrepresentableNetworkConfiguration(t *testing.T) {
	profile := testWireGuardConfig()
	_, peer, _ := strings.Cut(profile, "[Peer]")
	for _, invalid := range []string{
		strings.Replace(profile, "Address=10.2.0.2/32", "Address=10.2.0.2/32,10.3.0.2/32", 1),
		strings.Replace(profile, "Address=10.2.0.2/32", "Address=127.0.0.1/8", 1),
		strings.Replace(profile, "Address=10.2.0.2/32", "Address=::ffff:10.2.0.2/120", 1),
		strings.Replace(profile, "DNS=10.2.0.1", "DNS=2001:db8::1", 1),
		strings.Replace(profile, "DNS=10.2.0.1", "DNS=fe80::1%en0", 1),
		profile + "[Peer]" + peer,
	} {
		if _, err := parseWireGuardConfig(context.Background(), []byte(invalid)); err == nil {
			t.Fatal("unsupported configuration accepted")
		}
	}
}

func TestWireGuardHealthReadsOnlyRecentHandshakes(t *testing.T) {
	status := wireGuardHandshakeStatus{now: time.Unix(1000, 0)}
	snapshot := []byte("private_key=never-retain\npreshared_key=never-retain\nlast_handshake_time_sec=999\nlast_handshake_time_sec=0\nlast_handshake_time_sec=1\nlast_handshake_time_sec=1001\n")
	if n, err := status.Write(snapshot); err != nil || n != len(snapshot) || status.healthy != 1 {
		t.Fatalf("unexpected health: %v", status)
	}
}

func TestMemoryTUNCloseUnblocksFullQueuesAndRejectsLaterPackets(t *testing.T) {
	tun := newMemoryTUN(1280)
	transport := memoryPacketTransport{tun}
	for range 256 {
		_, _ = transport.Write([]byte{1})
		_, _ = tun.Write([][]byte{{1}}, 0)
	}
	done := make(chan error, 2)
	go func() { _, err := transport.Write([]byte{2}); done <- err }()
	go func() { _, err := tun.Write([][]byte{{2}}, 0); done <- err }()
	_ = tun.Close()
	for range 2 {
		select {
		case err := <-done:
			if !errors.Is(err, os.ErrClosed) {
				t.Fatal(err)
			}
		case <-time.After(time.Second):
			t.Fatal("blocked write survived close")
		}
	}
	for range 100 {
		if _, err := transport.Write([]byte{1}); !errors.Is(err, os.ErrClosed) {
			t.Fatal("closed transport accepted packet")
		}
		if _, err := transport.Read(make([]byte, 16)); !errors.Is(err, os.ErrClosed) {
			t.Fatal("closed transport returned queued packet")
		}
		if _, err := tun.Read([][]byte{make([]byte, 16)}, make([]int, 1), 0); !errors.Is(err, os.ErrClosed) {
			t.Fatal("closed TUN returned queued packet")
		}
		if _, err := tun.Write([][]byte{{1}}, 0); !errors.Is(err, os.ErrClosed) {
			t.Fatal("closed TUN accepted packet")
		}
	}
}

// Two actual WireGuard devices exercise encrypted UDP over loopback, not a mock
// cipher or external VPN. This catches framing and lifetime errors in memoryTUN.
func TestWireGuardLocalPeersCarryRawDatagrams(t *testing.T) {
	var vpns [2]*device.Device
	var transports [2]memoryPacketTransport
	var publics [2][]byte
	var ports [2]int
	for index := range vpns {
		private := make([]byte, 32)
		_, _ = rand.Read(private)
		public, err := curve25519.X25519(private, curve25519.Basepoint)
		if err != nil {
			t.Fatal(err)
		}
		publics[index] = public
		tun := newMemoryTUN(1280)
		vpn := device.NewDevice(tun, conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
		t.Cleanup(vpn.Close)
		vpns[index], transports[index] = vpn, memoryPacketTransport{tun}
		if err := vpn.IpcSet("private_key=" + hex.EncodeToString(private) + "\nlisten_port=0\n"); err != nil {
			t.Fatal("configure local device")
		}
		clearBytes(private)
		if err := vpn.Up(); err != nil {
			t.Fatal(err)
		}
		var port localWireGuardPort
		if err := vpn.IpcGetOperation(&port); err != nil || port.value == 0 {
			t.Fatal("local device did not bind")
		}
		ports[index] = port.value
	}
	for index, vpn := range vpns {
		other := 1 - index
		config := fmt.Sprintf("public_key=%s\nendpoint=%s\nallowed_ip=10.0.0.%d/32\n", hex.EncodeToString(publics[other]), net.JoinHostPort("127.0.0.1", strconv.Itoa(ports[other])), other+1)
		if err := vpn.IpcSet(config); err != nil {
			t.Fatal("configure local peer")
		}
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	for index, vpn := range vpns {
		var key device.NoisePublicKey
		copy(key[:], publics[1-index])
		if err := waitWireGuardHandshake(ctx, vpn, []device.NoisePublicKey{key}); err != nil {
			t.Fatal(err)
		}
	}
	for index := range transports {
		packet := make([]byte, 28)
		packet[0], packet[3], packet[8], packet[9] = 0x45, 28, 64, 17
		copy(packet[12:16], []byte{10, 0, 0, byte(index + 1)})
		copy(packet[16:20], []byte{10, 0, 0, byte(2 - index)})
		packet[21], packet[23], packet[25] = 1, 2, 8
		received := make(chan []byte, 1)
		go func(other int) {
			buffer := make([]byte, 1280)
			n, _ := transports[other].Read(buffer)
			received <- buffer[:n]
		}(1 - index)
		if _, err := transports[index].Write(packet); err != nil {
			t.Fatal(err)
		}
		select {
		case actual := <-received:
			if !bytes.Equal(packet, actual) {
				t.Fatal("raw IP datagram changed")
			}
		case <-ctx.Done():
			t.Fatal("encrypted loopback packet not delivered")
		}
	}
}

type localWireGuardPort struct{ value int }

func (port *localWireGuardPort) Write(snapshot []byte) (int, error) {
	for _, line := range bytes.Split(snapshot, []byte{'\n'}) {
		if bytes.HasPrefix(line, []byte("listen_port=")) {
			port.value, _ = strconv.Atoi(string(line[len("listen_port="):]))
		}
	}
	return len(snapshot), nil
}
