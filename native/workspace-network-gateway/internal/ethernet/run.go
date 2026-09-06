package ethernet

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"syscall"
	"time"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

// Run owns nic until return. nic must be a connected datagram socket; the
// runtime retains its own descriptor for the next route generation.
func Run(ctx context.Context, socketPath string, nic *net.UnixConn, keyInput io.Reader, ready io.Writer) error {
	if nic == nil {
		return errors.New("workspace NIC is required")
	}
	defer nic.Close()
	if !filepath.IsAbs(socketPath) || keyInput == nil || ready == nil {
		return errors.New("absolute packet socket, key input and readiness output required")
	}
	key, err := io.ReadAll(io.LimitReader(keyInput, protocol.AuthenticationKeyLength+1))
	defer clear(key)
	if err != nil || len(key) != protocol.AuthenticationKeyLength {
		return errors.New("authentication key must contain exactly 32 bytes followed by EOF")
	}
	parent, err := os.Stat(filepath.Dir(socketPath))
	if err != nil || !parent.IsDir() || parent.Mode().Perm()&0077 != 0 {
		return errors.New("provider socket directory must exist and be owner-private")
	}
	// Do not unlink any existing endpoint: only its owning generation may
	// remove it. A duplicate lease must fail rather than steal a live socket.
	listener, err := net.ListenUnix("unix", &net.UnixAddr{Name: socketPath, Net: "unix"})
	if err != nil {
		return fmt.Errorf("listen for provider: %w", err)
	}
	defer listener.Close()
	if err := os.Chmod(socketPath, 0600); err != nil {
		return fmt.Errorf("restrict provider socket: %w", err)
	}
	stopAccept := context.AfterFunc(ctx, func() { _ = listener.Close() })
	defer stopAccept()
	if _, err := fmt.Fprintln(ready, "READY v1"); err != nil {
		return err
	}
	connection, err := listener.AcceptUnix()
	if err != nil {
		return fmt.Errorf("accept provider: %w", err)
	}
	defer connection.Close()
	stopConnection := context.AfterFunc(ctx, func() { _ = connection.Close(); _ = nic.Close() })
	defer stopConnection()
	if err := connection.SetDeadline(time.Now().Add(30 * time.Second)); err != nil {
		return err
	}
	var r *router
	channel, err := protocol.ConnectGuest(connection, key, func(config protocol.NetworkConfiguration) error {
		var configureErr error
		r, configureErr = newRouter(config)
		return configureErr
	})
	clear(key)
	if r != nil {
		defer r.close()
	}
	if err != nil {
		return fmt.Errorf("establish provider channel: %w", err)
	}
	defer channel.Close()
	if err := connection.SetDeadline(time.Time{}); err != nil {
		return err
	}
	return pump(ctx, nic, channel, r)
}

func pump(ctx context.Context, nic *net.UnixConn, provider *protocol.Channel, r *router) error {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	results := make(chan error, 4)
	go func() {
		frame := make([]byte, MTU+15)
		for {
			n, _, err := nic.ReadFromUnix(frame)
			if err != nil {
				results <- fmt.Errorf("read workspace NIC: %w", err)
				return
			}
			r.injectFrame(frame[:n])
		}
	}()
	go func() {
		for {
			packet, err := provider.ReceivePacket()
			if err != nil {
				results <- fmt.Errorf("receive provider packet: %w", err)
				return
			}
			r.injectPacket(packet)
		}
	}()
	go func() { results <- writeFrames(ctx, nic, r) }()
	go func() {
		for {
			pkt := r.provider.ReadContext(ctx)
			if pkt == nil {
				results <- ctx.Err()
				return
			}
			view := pkt.ToView()
			err := provider.SendPacket(view.AsSlice())
			view.Release()
			pkt.DecRef()
			if err != nil {
				results <- fmt.Errorf("send provider packet: %w", err)
				return
			}
		}
	}()
	err := <-results
	cancel()
	_ = nic.Close()
	_ = provider.Interrupt()
	for range 3 {
		<-results
	}
	return err
}

func writeFrames(ctx context.Context, nic *net.UnixConn, r *router) error {
	for {
		pkt := r.guest.ReadContext(ctx)
		if pkt == nil {
			return ctx.Err()
		}
		view := pkt.ToView()
		err := writeFrameWithBackpressure(ctx, nic.Write, view.AsSlice())
		view.Release()
		pkt.DecRef()
		if err != nil {
			return fmt.Errorf("write workspace NIC: %w", err)
		}
	}
}

// Darwin's VZ datagram receiver reports ENOBUFS during ordinary traffic bursts.
// Congestion is packet loss, not a dead NIC. Retry the same frame for at most
// 31ms, then discard it; TCP endpoints recover through their normal retransmits.
// The bounded retry holds only this frame and never masks a disconnected peer.
func writeFrameWithBackpressure(ctx context.Context, write func([]byte) (int, error), frame []byte) error {
	for delay := time.Millisecond; ; delay *= 2 {
		if err := ctx.Err(); err != nil {
			return err
		}
		n, err := write(frame)
		if err == nil {
			if n != len(frame) {
				return io.ErrShortWrite
			}
			return nil
		}
		if !errors.Is(err, syscall.ENOBUFS) {
			return err
		}
		if delay > 16*time.Millisecond {
			return nil
		}
		timer := time.NewTimer(delay)
		select {
		case <-ctx.Done():
			timer.Stop()
			return ctx.Err()
		case <-timer.C:
		}
	}
}
