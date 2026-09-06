package ethernet

import (
	"bytes"
	"context"
	"errors"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"testing"
	"time"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
	"golang.org/x/sys/unix"
)

func TestNICCongestionDoesNotTerminateRoute(t *testing.T) {
	for _, recovers := range []bool{false, true} {
		calls := 0
		frame := []byte("one unchanged datagram")
		err := writeFrameWithBackpressure(context.Background(), func(value []byte) (int, error) {
			calls++
			if !bytes.Equal(value, frame) {
				t.Fatal("retry changed the frame")
			}
			if recovers && calls == 3 {
				return len(value), nil
			}
			return 0, &net.OpError{Op: "write", Err: syscall.ENOBUFS}
		}, frame)
		if err != nil || calls < 3 || calls > 6 {
			t.Fatalf("recovers=%v: calls=%d error=%v", recovers, calls, err)
		}
	}
}

func TestNICBackpressureHonorsCancellationAndPermanentFailure(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	err := writeFrameWithBackpressure(ctx, func([]byte) (int, error) {
		cancel()
		return 0, syscall.ENOBUFS
	}, []byte("frame"))
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation: %v", err)
	}
	for _, failure := range []error{syscall.ECONNREFUSED, io.ErrClosedPipe} {
		calls := 0
		err := writeFrameWithBackpressure(context.Background(), func([]byte) (int, error) {
			calls++
			return 0, failure
		}, []byte("frame"))
		if !errors.Is(err, failure) || calls != 1 {
			t.Fatalf("permanent failure: calls=%d error=%v", calls, err)
		}
	}
}

func TestRunRejectsInvalidKey(t *testing.T) {
	for _, size := range []int{0, 31, 33, 1024} {
		nic, peer := socketPair(t)
		defer peer.Close()
		err := Run(context.Background(), filepath.Join(t.TempDir(), "packets.sock"), nic, bytes.NewReader(make([]byte, size)), io.Discard)
		if err == nil || !strings.Contains(err.Error(), "exactly 32 bytes") {
			t.Fatalf("size %d: %v", size, err)
		}
	}
}

func TestRunCancellationAndDisconnectAreFailClosed(t *testing.T) {
	for _, stage := range []string{"accept", "handshake", "connected", "provider-disconnect"} {
		t.Run(stage, func(t *testing.T) {
			nic, peer := socketPair(t)
			defer peer.Close()
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			key := bytes.Repeat([]byte{42}, 32)
			socket := testSocketPath(t)
			readyReader, readyWriter := io.Pipe()
			defer readyReader.Close()
			results := make(chan error, 1)
			go func() { results <- Run(ctx, socket, nic, bytes.NewReader(key), readyWriter); _ = readyWriter.Close() }()
			ready := make([]byte, len("READY v1\n"))
			if _, err := io.ReadFull(readyReader, ready); err != nil {
				t.Fatalf("read readiness: %v, runner: %v", err, <-results)
			}
			if string(ready) != "READY v1\n" {
				t.Fatalf("readiness %q", ready)
			}
			if info, err := os.Stat(socket); err != nil || info.Mode().Perm() != 0600 {
				t.Fatalf("unsafe endpoint permissions: %v %v", info, err)
			}
			var provider net.Conn
			if stage != "accept" {
				var err error
				provider, err = net.Dial("unix", socket)
				if err != nil {
					t.Fatal(err)
				}
				defer provider.Close()
			}
			if stage == "connected" || stage == "provider-disconnect" {
				prefix := netip.MustParsePrefix("10.20.30.2/32")
				channel, err := protocol.ConnectHost(provider, key, protocol.NetworkConfiguration{InterfaceName: "route0", MTU: MTU, IPv4Address: &prefix, DNSServers: []netip.Addr{netip.MustParseAddr("192.0.2.53")}})
				if err != nil {
					t.Fatal(err)
				}
				defer channel.Close()
			}
			if stage == "provider-disconnect" {
				_ = provider.Close()
			} else {
				cancel()
			}
			select {
			case err := <-results:
				if err == nil {
					t.Fatal("expected explicit route termination error")
				}
			case <-time.After(2 * time.Second):
				stacks := make([]byte, 1<<20)
				t.Fatalf("route did not terminate\n%s", stacks[:runtime.Stack(stacks, true)])
			}
			if _, err := os.Stat(socket); !os.IsNotExist(err) {
				t.Fatalf("provider socket survived teardown: %v", err)
			}
			_ = peer.SetWriteDeadline(time.Now().Add(50 * time.Millisecond))
			if _, err := peer.Write([]byte("cannot reach any uplink")); err == nil {
				t.Fatal("NIC still consumed packets after teardown")
			}
		})
	}
}

func TestRunDoesNotReplaceExistingSocket(t *testing.T) {
	socket := testSocketPath(t)
	listener, err := net.Listen("unix", socket)
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	nic, peer := socketPair(t)
	defer peer.Close()
	if err := Run(context.Background(), socket, nic, bytes.NewReader(make([]byte, 32)), io.Discard); err == nil {
		t.Fatal("stole existing route endpoint")
	}
	connection, err := net.Dial("unix", socket)
	if err != nil {
		t.Fatalf("existing listener was lost: %v", err)
	}
	_ = connection.Close()
}

func testSocketPath(t *testing.T) string {
	t.Helper()
	// Darwin's sockaddr_un path limit is shorter than testing.TempDir paths.
	dir, err := os.MkdirTemp("/tmp", "gs-ethernet-")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		if err := os.RemoveAll(dir); err != nil {
			t.Error(err)
		}
	})
	return filepath.Join(dir, "packet.sock")
}

func socketPair(t *testing.T) (*net.UnixConn, *net.UnixConn) {
	t.Helper()
	fds, err := unix.Socketpair(unix.AF_UNIX, unix.SOCK_DGRAM, 0)
	if err != nil {
		t.Fatal(err)
	}
	connections := make([]*net.UnixConn, 2)
	for i, fd := range fds {
		file := os.NewFile(uintptr(fd), "test-nic")
		connection, err := net.FileConn(file)
		_ = file.Close()
		if err != nil {
			t.Fatal(err)
		}
		connections[i] = connection.(*net.UnixConn)
	}
	return connections[0], connections[1]
}
