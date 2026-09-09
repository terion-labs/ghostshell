//go:build linux

package relay

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"

	"golang.org/x/sys/unix"
)

// RunTap is the only privileged relay process. It creates a nonpersistent TAP
// and moves frames, never dials a destination. EOF closes the TAP and its routes.
func RunTap(ctx context.Context) error {
	fd, err := unix.Open("/dev/net/tun", unix.O_RDWR|unix.O_NONBLOCK|unix.O_CLOEXEC, 0)
	if err != nil {
		return err
	}
	defer unix.Close(fd)
	request, err := unix.NewIfreq("relay0")
	if err != nil {
		return err
	}
	request.SetUint16(unix.IFF_TAP | unix.IFF_NO_PI)
	if err := unix.IoctlIfreq(fd, unix.TUNSETIFF, request); err != nil {
		return err
	}
	// Docker starts this namespace with loopback only. No route is usable until
	// the host has authenticated its own gateway and begun processing frames.
	commands := [][]string{
		{"link", "set", "relay0", "address", "02:47:53:4e:57:02", "mtu", "1500", "up"},
		{"address", "add", "100.64.0.2/30", "dev", "relay0"},
		{"-6", "address", "add", "fd00:4753:4e57::2/126", "dev", "relay0", "nodad"},
		{"route", "add", "default", "via", "100.64.0.1"},
		{"-6", "route", "add", "default", "via", "fd00:4753:4e57::1"},
	}
	for _, args := range commands {
		if err := exec.CommandContext(ctx, "/usr/sbin/ip", args...).Run(); err != nil {
			return fmt.Errorf("configure relay TAP: %w", err)
		}
	}
	if _, err := io.WriteString(os.Stdout, "READY relay-v1\n"); err != nil {
		return err
	}
	results := make(chan error, 2)
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	// The command owns these inherited pipes. Poll them too, so cancellation
	// can join both pumps even when the peer leaves stdin open without data.
	for _, pipe := range []int{unix.Stdin, unix.Stdout} {
		if err := unix.SetNonblock(pipe, true); err != nil {
			return err
		}
	}
	input, output := relayPipe{ctx, unix.Stdin}, relayPipe{ctx, unix.Stdout}
	go func() {
		frame := make([]byte, maximumFrame+1)
		for {
			n, err := tapIO(ctx, fd, frame, false)
			if err == nil {
				err = writeFrame(output, frame[:n])
			}
			if err != nil {
				results <- err
				return
			}
		}
	}()
	go func() {
		frame := make([]byte, maximumFrame)
		for {
			n, err := readFrame(input, frame)
			if err == nil {
				var written int
				written, err = tapIO(ctx, fd, frame[:n], true)
				if err == nil && written != n {
					err = io.ErrShortWrite
				}
			}
			if err != nil {
				results <- err
				return
			}
		}
	}()
	err = <-results
	cancel()
	<-results
	return err
}

type relayPipe struct {
	ctx context.Context
	fd  int
}

func (p relayPipe) Read(b []byte) (int, error)  { return tapIO(p.ctx, p.fd, b, false) }
func (p relayPipe) Write(b []byte) (int, error) { return tapIO(p.ctx, p.fd, b, true) }

// Use Linux poll directly rather than Go's epoll-backed os.File adapter: some
// x64 emulators reject registering TUN with epoll. Nonblocking I/O and a bounded
// poll also let cancellation interrupt an otherwise idle TAP.
func tapIO(ctx context.Context, fd int, frame []byte, write bool) (int, error) {
	for ctx.Err() == nil {
		var n int
		var err error
		events := int16(unix.POLLIN)
		if write {
			n, err = unix.Write(fd, frame)
			events = unix.POLLOUT
		} else {
			n, err = unix.Read(fd, frame)
			if n == 0 && err == nil {
				err = io.EOF
			}
		}
		if err == nil || (!errors.Is(err, unix.EAGAIN) && !errors.Is(err, unix.EINTR)) {
			return n, err
		}
		if _, err := unix.Poll([]unix.PollFd{{Fd: int32(fd), Events: events}}, 100); err != nil && !errors.Is(err, unix.EINTR) {
			return 0, err
		}
	}
	return 0, ctx.Err()
}
