package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"net"
	"net/netip"
	"os"
	"os/signal"
	"syscall"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/ethernet"
	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/guest"
	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/host"
)

func main() {
	if err := run(); err != nil {
		_, _ = fmt.Fprintf(os.Stderr, "workspace network gateway: %v\n", err)
		os.Exit(1)
	}
}

func run() error {
	if len(os.Args) < 2 {
		return usageError()
	}
	switch os.Args[1] {
	case "ethernet":
		return runEthernet(os.Args[2:])
	case "guest":
		return runGuest(os.Args[2:])
	case "guest-stop":
		return runGuestStop(os.Args[2:])
	case "init":
		return runInit(os.Args[2:])
	case "host":
		return runHost(os.Args[2:])
	case "openconnect-vpnfd":
		return runOpenConnect(os.Args[2:])
	case "openconnect-socks":
		return runOpenConnectSOCKS(os.Args[2:])
	case "wireguard":
		return runWireGuard(os.Args[2:])
	case "openvpn":
		return runOpenVPN(os.Args[2:])
	default:
		return usageError()
	}
}

func runEthernet(arguments []string) error {
	flags := flag.NewFlagSet("ethernet", flag.ContinueOnError)
	socket := flags.String("socket", "", "owner-private provider packet socket")
	if err := flags.Parse(arguments); err != nil {
		return err
	}
	if flags.NArg() != 0 {
		return errors.New("ethernet mode does not accept positional arguments")
	}
	file := os.NewFile(3, "workspace-nic")
	if file == nil {
		return errors.New("workspace NIC descriptor 3 is required")
	}
	connection, err := net.FileConn(file)
	_ = file.Close()
	if err != nil {
		return fmt.Errorf("open workspace NIC descriptor: %w", err)
	}
	nic, ok := connection.(*net.UnixConn)
	if !ok || nic.LocalAddr().Network() != "unixgram" || nic.RemoteAddr() == nil {
		_ = connection.Close()
		return errors.New("workspace NIC must be a connected Unix datagram socket")
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM, syscall.SIGHUP)
	defer stop()
	return ethernet.Run(ctx, *socket, nic, os.Stdin, os.Stdout)
}

func runOpenVPN(arguments []string) error {
	flags := flag.NewFlagSet("openvpn", flag.ContinueOnError)
	config := flags.String("config", "", "owner-private OpenVPN configuration file")
	engine := flags.String("engine", "", "bundled OpenVPN Core engine executable")
	username := flags.String("username", "", "OpenVPN username")
	socket := flags.String("socket", "", "guest packet socket")
	key := flags.String("key-file", "", "owner-private packet authentication key")
	port := flags.Uint("socks-port", 0, "host projection loopback port")
	if err := flags.Parse(arguments); err != nil {
		return err
	}
	if flags.NArg() != 0 || *port > 65535 {
		return errors.New("invalid OpenVPN arguments")
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM, syscall.SIGHUP)
	defer stop()
	return host.RunOpenVPN(ctx, host.VPNRouteOptions{ConfigPath: *config, SocketPath: *socket, KeyFilePath: *key, SOCKSPort: uint16(*port), Ready: os.Stdout}, *engine, *username, os.Stdin)
}

func runWireGuard(arguments []string) error {
	flags := flag.NewFlagSet("wireguard", flag.ContinueOnError)
	config := flags.String("config", "", "owner-private WireGuard configuration file")
	socket := flags.String("socket", "", "guest packet socket")
	key := flags.String("key-file", "", "owner-private packet authentication key")
	port := flags.Uint("socks-port", 0, "host projection loopback port")
	if err := flags.Parse(arguments); err != nil {
		return err
	}
	if flags.NArg() != 0 || *port > 65535 {
		return errors.New("invalid WireGuard arguments")
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM, syscall.SIGHUP)
	defer stop()
	return host.RunWireGuard(ctx, host.VPNRouteOptions{ConfigPath: *config, SocketPath: *socket, KeyFilePath: *key, SOCKSPort: uint16(*port), Ready: os.Stdout})
}

func runOpenConnectSOCKS(arguments []string) error {
	flags := flag.NewFlagSet("openconnect-socks", flag.ContinueOnError)
	port := flags.Uint("port", 0, "loopback SOCKS5 listener port")
	if err := flags.Parse(arguments); err != nil {
		return err
	}
	if flags.NArg() != 0 || *port == 0 || *port > 65535 {
		return errors.New("openconnect-socks requires a valid --port")
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM, syscall.SIGHUP)
	defer stop()
	return host.RunOpenConnectSOCKS(ctx, uint16(*port), os.Getenv, os.Stdout)
}

func runOpenConnect(arguments []string) error {
	flags := flag.NewFlagSet("openconnect-vpnfd", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	socketPath := flags.String("socket", "", "absolute published guest Unix socket path")
	keyFilePath := flags.String("key-file", "", "absolute owner-only authentication key file path")
	if err := flags.Parse(arguments); err != nil {
		return err
	}
	if flags.NArg() != 0 {
		return errors.New("openconnect-vpnfd mode does not accept positional arguments")
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM, syscall.SIGHUP)
	defer stop()
	return host.RunOpenConnect(ctx, host.OpenConnectOptions{
		SocketPath: *socketPath, KeyFilePath: *keyFilePath,
		Environment: os.Getenv, Ready: os.Stdout,
	})
}

type addressList []netip.Addr

func (addresses *addressList) String() string { return fmt.Sprint([]netip.Addr(*addresses)) }

func (addresses *addressList) Set(text string) error {
	address, err := netip.ParseAddr(text)
	if err != nil {
		return fmt.Errorf("invalid IP address %q", text)
	}
	*addresses = append(*addresses, address)
	return nil
}

func runHost(arguments []string) error {
	flags := flag.NewFlagSet("host", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	socketPath := flags.String("socket", "", "absolute published host Unix socket path")
	mode := flags.String("mode", "direct", "direct, socks5, http, or https")
	upstreamHost := flags.String("upstream-host", "", "proxy address")
	upstreamPort := flags.Uint("upstream-port", 0, "proxy port")
	mtu := flags.Uint("mtu", 1280, "guest TUN MTU")
	doh := flags.Bool("dns-over-https", false, "translate guest DNS to HTTPS inside the selected proxy")
	names := flags.Bool("resolve-proxy-names", false, "resolve service-VM names at the SOCKS5 endpoint using private synthetic DNS")
	udp := flags.Bool("allow-udp-associate", false, "verify and enable an owned loopback SOCKS5 UDP route")
	var dns addressList
	flags.Var(&dns, "dns", "DNS server IP address (repeatable)")
	if err := flags.Parse(arguments); err != nil {
		return err
	}
	if flags.NArg() != 0 {
		return errors.New("host mode does not accept positional arguments")
	}
	if *upstreamPort > 65535 || *mtu > 65535 {
		return errors.New("port and MTU must fit in 16 bits")
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	return host.Run(ctx, host.Options{
		SocketPath: *socketPath, Mode: *mode, UpstreamHost: *upstreamHost,
		UpstreamPort: uint16(*upstreamPort), MTU: uint16(*mtu), DNSServers: dns,
		DNSOverHTTPS:      *doh,
		ResolveProxyNames: *names,
		AllowUDPAssociate: *udp,
		KeyInput:          os.Stdin, Ready: os.Stdout,
	})
}

func runGuest(arguments []string) error {
	flags := flag.NewFlagSet("guest", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	socketPath := flags.String("socket", "", "absolute guest Unix socket path")
	pidFilePath := flags.String("pid-file", "", "absolute guest PID file path")
	gatewayText := flags.String("gateway", "", "Apple host-only IPv4 gateway")
	if err := flags.Parse(arguments); err != nil {
		return err
	}
	if flags.NArg() != 0 {
		return errors.New("guest mode does not accept positional arguments")
	}
	gateway, err := netip.ParseAddr(*gatewayText)
	if err != nil || !gateway.Is4() {
		return errors.New("--gateway must be an IPv4 address")
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	return guest.Run(ctx, guest.Options{
		SocketPath:  *socketPath,
		PidFilePath: *pidFilePath,
		Gateway:     gateway,
		KeyInput:    os.Stdin,
		Ready:       os.Stdout,
	})
}

func runGuestStop(arguments []string) error {
	flags := flag.NewFlagSet("guest-stop", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	socketPath := flags.String("socket", "", "absolute guest Unix socket path")
	pidFilePath := flags.String("pid-file", "", "absolute guest PID file path")
	if err := flags.Parse(arguments); err != nil {
		return err
	}
	if flags.NArg() != 0 {
		return errors.New("guest-stop mode does not accept positional arguments")
	}
	return guest.Stop(guest.StopOptions{
		SocketPath:  *socketPath,
		PidFilePath: *pidFilePath,
	})
}

func runInit(arguments []string) error {
	flags := flag.NewFlagSet("init", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	gatewayText := flags.String("gateway", "", "Apple host-only IPv4 gateway")
	if err := flags.Parse(arguments); err != nil {
		return err
	}
	command := flags.Args()
	if len(command) == 0 {
		return errors.New("init mode requires a command after --")
	}
	gateway, err := netip.ParseAddr(*gatewayText)
	if err != nil || !gateway.Is4() {
		return errors.New("--gateway must be an IPv4 address")
	}
	return guest.RunInit(gateway, command)
}

func usageError() error {
	return errors.New("usage: workspace-gateway ethernet|host|guest|guest-stop|init|openconnect-vpnfd|openconnect-socks|wireguard|openvpn [options]")
}
