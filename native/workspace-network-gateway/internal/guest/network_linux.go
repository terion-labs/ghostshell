//go:build linux

package guest

import (
	"errors"
	"fmt"
	"net/netip"
	"os"
	"os/exec"
	"strings"
	"syscall"
	"unsafe"

	"github.com/terion-labs/asura/native/workspace-network-gateway/internal/protocol"
)

const (
	tunSetInterface           = uintptr(0x400454ca)
	interfaceNameSize         = 16
	interfaceRequestSize      = 40
	interfaceFlagTun          = uint16(0x0001)
	interfaceFlagNoPacketInfo = uint16(0x1000)
)

type interfaceRequest struct {
	Name  [interfaceNameSize]byte
	Flags uint16
	Pad   [interfaceRequestSize - interfaceNameSize - 2]byte
}

func lockDownControlNetwork(gateway netip.Addr) error {
	output, err := runIP("-4", "-o", "route", "get", gateway.String())
	if err != nil {
		return fmt.Errorf("locate control gateway route: %w", err)
	}
	device, err := routeDevice(output)
	if err != nil {
		return err
	}

	if _, err := runIP("-4", "route", "flush", "table", "main"); err != nil {
		return fmt.Errorf("remove direct IPv4 routes: %w", err)
	}
	if _, err := runIP("-6", "route", "flush", "table", "main"); err != nil {
		return fmt.Errorf("remove direct IPv6 routes: %w", err)
	}
	if _, err := runIP("-4", "route", "replace", gateway.String()+"/32", "dev", device, "scope", "link"); err != nil {
		return fmt.Errorf("preserve control gateway route: %w", err)
	}
	return nil
}

func configureTunnel(configuration protocol.NetworkConfiguration) (packetDevice, error) {
	device, actualName, err := openTunnel(configuration.InterfaceName)
	if err != nil {
		return nil, err
	}
	fail := func(cause error) (packetDevice, error) {
		_ = device.Close()
		_, _ = runIP("-4", "route", "flush", "default")
		_, _ = runIP("-6", "route", "flush", "default")
		return nil, cause
	}

	if configuration.IPv4Address != nil {
		if _, err := runIP("-4", "address", "add", configuration.IPv4Address.String(), "dev", actualName); err != nil {
			return fail(fmt.Errorf("assign TUN IPv4 address: %w", err))
		}
	}
	if configuration.IPv6Address != nil {
		if _, err := runIP("-6", "address", "add", configuration.IPv6Address.String(), "dev", actualName); err != nil {
			return fail(fmt.Errorf("assign TUN IPv6 address: %w", err))
		}
	}
	if _, err := runIP("link", "set", "dev", actualName, "mtu", fmt.Sprint(configuration.MTU), "up"); err != nil {
		return fail(fmt.Errorf("bring up guest TUN: %w", err))
	}
	if configuration.IPv4Address != nil {
		if _, err := runIP("-4", "route", "replace", "default", "dev", actualName); err != nil {
			return fail(fmt.Errorf("install TUN IPv4 default route: %w", err))
		}
	}
	if configuration.IPv6Address != nil {
		if _, err := runIP("-6", "route", "replace", "default", "dev", actualName); err != nil {
			return fail(fmt.Errorf("install TUN IPv6 default route: %w", err))
		}
	}
	if err := writeResolverConfiguration(configuration.DNSServers); err != nil {
		return fail(err)
	}
	return device, nil
}

func openTunnel(requestedName string) (*os.File, string, error) {
	device, err := os.OpenFile("/dev/net/tun", os.O_RDWR, 0)
	if err != nil {
		return nil, "", fmt.Errorf("open /dev/net/tun: %w", err)
	}
	request := interfaceRequest{Flags: interfaceFlagTun | interfaceFlagNoPacketInfo}
	copy(request.Name[:], requestedName)
	_, _, errno := syscall.Syscall(
		syscall.SYS_IOCTL,
		device.Fd(),
		tunSetInterface,
		uintptr(unsafe.Pointer(&request)))
	if errno != 0 {
		_ = device.Close()
		return nil, "", fmt.Errorf("create TUN interface: %w", errno)
	}
	actualName := strings.TrimRight(string(request.Name[:]), "\x00")
	if actualName == "" {
		_ = device.Close()
		return nil, "", errors.New("kernel returned an empty TUN interface name")
	}
	return device, actualName, nil
}

func runIP(arguments ...string) (string, error) {
	command := exec.Command("ip", arguments...)
	command.Env = append(os.Environ(), "LC_ALL=C")
	output, err := command.CombinedOutput()
	if err != nil {
		return "", fmt.Errorf("ip command failed: %w", err)
	}
	return string(output), nil
}

func execInitProcess(command []string) error {
	return syscall.Exec(command[0], command, os.Environ())
}

func routeDevice(output string) (string, error) {
	fields := strings.Fields(output)
	for index := 0; index+1 < len(fields); index++ {
		if fields[index] == "dev" && validDeviceName(fields[index+1]) {
			return fields[index+1], nil
		}
	}
	return "", errors.New("control gateway route has no valid device")
}

func validDeviceName(value string) bool {
	if value == "" || len(value) >= interfaceNameSize || value == "." || value == ".." {
		return false
	}
	for _, character := range value {
		if character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
			character >= '0' && character <= '9' || character == '.' || character == '_' || character == '-' {
			continue
		}
		return false
	}
	return true
}

func writeResolverConfiguration(servers []netip.Addr) error {
	var builder strings.Builder
	for _, server := range servers {
		builder.WriteString("nameserver ")
		builder.WriteString(server.String())
		builder.WriteByte('\n')
	}
	file, err := os.OpenFile("/etc/resolv.conf", os.O_WRONLY|os.O_TRUNC, 0)
	if err != nil {
		return fmt.Errorf("open resolver configuration: %w", err)
	}
	if _, err := file.WriteString(builder.String()); err != nil {
		_ = file.Close()
		return fmt.Errorf("write resolver configuration: %w", err)
	}
	if err := file.Sync(); err != nil {
		_ = file.Close()
		return fmt.Errorf("sync resolver configuration: %w", err)
	}
	if err := file.Close(); err != nil {
		return fmt.Errorf("close resolver configuration: %w", err)
	}
	return nil
}
