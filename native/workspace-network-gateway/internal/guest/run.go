package guest

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

type Options struct {
	SocketPath  string
	PidFilePath string
	Gateway     netip.Addr
	KeyInput    io.Reader
	Ready       io.Writer
}

type StopOptions struct {
	SocketPath  string
	PidFilePath string
}

func RunInit(gateway netip.Addr, command []string) error {
	if !gateway.Is4() {
		return errors.New("Apple control gateway must be an IPv4 address")
	}
	if len(command) == 0 || !filepath.IsAbs(command[0]) {
		return errors.New("init command must use an absolute executable path")
	}
	if err := lockDownControlNetwork(gateway); err != nil {
		return fmt.Errorf("lock down Apple control network: %w", err)
	}
	return execInitProcess(command)
}

func Run(ctx context.Context, options Options) error {
	if ctx == nil {
		return errors.New("context is required")
	}
	if options.SocketPath == "" || !filepath.IsAbs(options.SocketPath) {
		return errors.New("guest socket path must be absolute")
	}
	if options.PidFilePath == "" || !filepath.IsAbs(options.PidFilePath) {
		return errors.New("guest PID file path must be absolute")
	}
	if !options.Gateway.Is4() {
		return errors.New("Apple control gateway must be an IPv4 address")
	}
	if options.KeyInput == nil || options.Ready == nil {
		return errors.New("key input and readiness output are required")
	}

	authenticationKey, err := readAuthenticationKey(options.KeyInput)
	if err != nil {
		return err
	}
	defer clearBytes(authenticationKey)
	pidFile, err := claimPidFile(options.PidFilePath)
	if err != nil {
		return err
	}
	defer pidFile.release()

	if err := lockDownControlNetwork(options.Gateway); err != nil {
		return fmt.Errorf("lock down Apple control network: %w", err)
	}
	listener, err := listen(options.SocketPath)
	if err != nil {
		return err
	}
	defer func() {
		_ = listener.Close()
		_ = os.Remove(options.SocketPath)
	}()

	acceptCancelled := make(chan struct{})
	go func() {
		select {
		case <-ctx.Done():
			_ = listener.Close()
		case <-acceptCancelled:
		}
	}()
	defer close(acceptCancelled)
	if _, err := fmt.Fprintln(options.Ready, "READY v1"); err != nil {
		return fmt.Errorf("announce readiness: %w", err)
	}

	connection, err := listener.AcceptUnix()
	if err != nil {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		return fmt.Errorf("accept host packet channel: %w", err)
	}
	defer connection.Close()

	var device packetDevice
	channel, err := protocol.ConnectGuest(
		connection,
		authenticationKey,
		func(configuration protocol.NetworkConfiguration) error {
			configured, configureErr := configureTunnel(configuration)
			if configureErr == nil {
				device = configured
			}
			return configureErr
		})
	clearBytes(authenticationKey)
	if err != nil {
		return fmt.Errorf("establish packet channel: %w", err)
	}
	defer channel.Close()
	defer device.Close()

	pumpCancelled := make(chan struct{})
	go func() {
		select {
		case <-ctx.Done():
			_ = connection.Close()
			_ = device.Close()
		case <-pumpCancelled:
		}
	}()
	defer close(pumpCancelled)
	return pump(device, channel)
}

func Stop(options StopOptions) error {
	if options.SocketPath == "" || !filepath.IsAbs(options.SocketPath) {
		return errors.New("guest socket path must be absolute")
	}
	if options.PidFilePath == "" || !filepath.IsAbs(options.PidFilePath) {
		return errors.New("guest PID file path must be absolute")
	}
	return stopOwnedGuest(options.PidFilePath, options.SocketPath)
}

func readAuthenticationKey(input io.Reader) ([]byte, error) {
	key := make([]byte, protocol.AuthenticationKeyLength)
	if _, err := io.ReadFull(input, key); err != nil {
		clearBytes(key)
		return nil, fmt.Errorf("read authentication key: %w", err)
	}
	var extra [1]byte
	if count, err := input.Read(extra[:]); count != 0 || !errors.Is(err, io.EOF) {
		clearBytes(key)
		return nil, errors.New("authentication key input must contain exactly 32 bytes followed by EOF")
	}
	return key, nil
}

func listen(socketPath string) (*net.UnixListener, error) {
	if err := os.MkdirAll(filepath.Dir(socketPath), 0o700); err != nil {
		return nil, fmt.Errorf("create packet socket directory: %w", err)
	}
	if information, err := os.Lstat(socketPath); err == nil {
		if information.Mode()&os.ModeSocket == 0 {
			return nil, errors.New("packet socket path is occupied by a non-socket file")
		}
		if err := os.Remove(socketPath); err != nil {
			return nil, fmt.Errorf("remove stale packet socket: %w", err)
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return nil, fmt.Errorf("inspect packet socket: %w", err)
	}

	listener, err := net.ListenUnix("unix", &net.UnixAddr{Name: socketPath, Net: "unix"})
	if err != nil {
		return nil, fmt.Errorf("listen on packet socket: %w", err)
	}
	if err := os.Chmod(socketPath, 0o600); err != nil {
		_ = listener.Close()
		return nil, fmt.Errorf("restrict packet socket permissions: %w", err)
	}
	return listener, nil
}

func pump(device packetDevice, channel *protocol.Channel) error {
	errorsChannel := make(chan error, 2)
	go func() {
		buffer := make([]byte, protocol.MaximumPayloadLength)
		for {
			count, err := device.Read(buffer)
			if err != nil {
				errorsChannel <- fmt.Errorf("read guest TUN: %w", err)
				return
			}
			if err := channel.SendPacket(buffer[:count]); err != nil {
				errorsChannel <- fmt.Errorf("send guest packet: %w", err)
				return
			}
		}
	}()
	go func() {
		for {
			packet, err := channel.ReceivePacket()
			if err != nil {
				errorsChannel <- fmt.Errorf("receive host packet: %w", err)
				return
			}
			count, err := device.Write(packet)
			clearBytes(packet)
			if err != nil {
				errorsChannel <- fmt.Errorf("write guest TUN: %w", err)
				return
			}
			if count != len(packet) {
				errorsChannel <- io.ErrShortWrite
				return
			}
		}
	}()

	err := <-errorsChannel
	_ = device.Close()
	_ = channel.Interrupt()
	<-errorsChannel
	return err
}

type packetDevice interface {
	io.ReadWriteCloser
}

func clearBytes(value []byte) {
	for index := range value {
		value[index] = 0
	}
}
