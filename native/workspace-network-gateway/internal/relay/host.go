package relay

import (
	"context"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"time"

	"github.com/terion-labs/asura/native/workspace-network-gateway/internal/ethernet"
	"golang.org/x/sys/unix"
)

// RunHost owns the exec process and its NIC. Losing either stream tears down
// the gateway. A container without this channel has no external interface.
func RunHost(ctx context.Context, socket string, command []string, key io.Reader, ready io.Writer) error {
	if len(command) == 0 {
		return fmt.Errorf("relay exec command is required")
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	pair, err := unix.Socketpair(unix.AF_UNIX, unix.SOCK_DGRAM, 0)
	if err != nil {
		return err
	}
	connections := make([]*net.UnixConn, 0, 2)
	files := []*os.File{os.NewFile(uintptr(pair[0]), "relay-nic"), os.NewFile(uintptr(pair[1]), "relay-wire")}
	defer files[0].Close()
	defer files[1].Close()
	for _, file := range files {
		conn, openErr := net.FileConn(file)
		_ = file.Close()
		if openErr != nil {
			for _, c := range connections {
				_ = c.Close()
			}
			return openErr
		}
		connections = append(connections, conn.(*net.UnixConn))
	}
	nic, wire := connections[0], connections[1]
	defer nic.Close()
	defer wire.Close()
	child := exec.CommandContext(ctx, command[0], command[1:]...)
	child.Stderr = os.Stderr
	child.WaitDelay = 2 * time.Second
	input, err := child.StdinPipe()
	if err != nil {
		return err
	}
	output, err := child.StdoutPipe()
	if err != nil {
		_ = input.Close()
		return err
	}
	if err := child.Start(); err != nil {
		return err
	}
	defer func() { cancel(); _ = input.Close(); _ = output.Close(); _ = child.Wait() }()
	// Do not report a ready host gateway before the container actually has its
	// TAP. Exec startup can be slow on a cold engine or under x64 emulation.
	initialized := make(chan error, 1)
	go func() {
		magic := make([]byte, len("READY relay-v1\n"))
		_, err := io.ReadFull(output, magic)
		if err == nil && string(magic) != "READY relay-v1\n" {
			err = fmt.Errorf("invalid relay TAP readiness")
		}
		initialized <- err
	}()
	timer := time.NewTimer(25 * time.Second)
	defer timer.Stop()
	select {
	case err := <-initialized:
		if err != nil {
			return fmt.Errorf("initialize relay TAP: %w", err)
		}
	case <-ctx.Done():
		_ = output.Close()
		<-initialized
		return ctx.Err()
	case <-timer.C:
		_ = output.Close()
		<-initialized
		return fmt.Errorf("relay TAP readiness timed out")
	}
	results := make(chan error, 3)
	go func() { results <- ethernet.Run(ctx, socket, nic, key, ready) }()
	go func() {
		frame := make([]byte, maximumFrame+1)
		for {
			n, err := wire.Read(frame)
			if err == nil {
				err = writeFrame(input, frame[:n])
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
			n, err := readFrame(output, frame)
			if err == nil {
				_, err = wire.Write(frame[:n])
			}
			if err != nil {
				results <- err
				return
			}
		}
	}()
	err = <-results
	cancel()
	_ = input.Close()
	_ = output.Close()
	_ = wire.Close()
	_ = nic.Close()
	<-results
	<-results
	return fmt.Errorf("relay packet transport closed: %w", err)
}
