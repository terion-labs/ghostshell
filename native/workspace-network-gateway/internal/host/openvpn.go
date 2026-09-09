package host

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/netip"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"

	"golang.org/x/sys/unix"

	"github.com/terion-labs/asura/native/workspace-network-gateway/internal/protocol"
)

// RunOpenVPN owns one bundled Core process and projects its authenticated raw IP
// socket to a workspace. Neither this bridge nor the engine creates host routes.
func RunOpenVPN(ctx context.Context, options VPNRouteOptions, enginePath, username string, password io.Reader) error {
	if ctx == nil {
		return errors.New("OpenVPN context is required")
	}
	if err := options.validate(); err != nil {
		return err
	}
	engine, err := startOpenVPNEngine(ctx, enginePath, options.ConfigPath, username, password)
	if err != nil {
		return err
	}
	defer engine.Close()
	routeCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	routed := make(chan error, 1)
	go func() { routed <- runVPNRoute(routeCtx, options, engine.packets, engine.config, engine.policy) }()
	select {
	case err = <-routed:
		return err
	case <-engine.done:
		err = errors.New("OpenVPN session stopped")
		if engine.diagnostics.authenticationFailed {
			err = errors.New("OpenVPN authentication failed")
		}
	case <-ctx.Done():
		err = ctx.Err()
	}
	cancel()
	_ = engine.packets.Close()
	<-routed
	return err
}

type openVPNEngine struct {
	packets     net.Conn
	config      protocol.NetworkConfiguration
	policy      openConnectRoutePolicy
	cancel      context.CancelFunc
	done        chan struct{}
	readDone    chan struct{}
	once        sync.Once
	diagnostics *openVPNDiagnostics
}

func (engine *openVPNEngine) Close() {
	engine.once.Do(func() {
		engine.cancel()
		_ = engine.packets.Close()
		<-engine.done
		<-engine.readDone
	})
}

func startOpenVPNEngine(ctx context.Context, enginePath, profilePath, username string, password io.Reader) (*openVPNEngine, error) {
	if !filepath.IsAbs(enginePath) || !filepath.IsAbs(profilePath) || len(username) > 4096 || strings.ContainsAny(username, "\x00\r\n") {
		return nil, errors.New("OpenVPN engine arguments are invalid")
	}
	if password == nil {
		password = strings.NewReader("")
	}
	credential, err := io.ReadAll(io.LimitReader(password, 8194))
	defer clearBytes(credential)
	if err != nil || len(credential) > 8193 {
		return nil, errors.New("OpenVPN credential input is invalid")
	}
	credential = bytes.TrimSuffix(credential, []byte{'\n'})
	if len(credential) > 8192 || bytes.ContainsAny(credential, "\x00\r\n") {
		return nil, errors.New("OpenVPN credential input is invalid")
	}
	input := append(append([]byte(nil), credential...), '\n')
	defer clearBytes(input)
	// Darwin lacks SOCK_CLOEXEC in socketpair. Hold the standard fork lock while
	// setting close-on-exec so concurrent application launches cannot inherit it.
	syscall.ForkLock.RLock()
	fds, err := unix.Socketpair(unix.AF_UNIX, unix.SOCK_DGRAM, 0)
	if err == nil {
		unix.CloseOnExec(fds[0])
		unix.CloseOnExec(fds[1])
	}
	syscall.ForkLock.RUnlock()
	if err != nil {
		return nil, errors.New("OpenVPN packet socket could not be created")
	}
	parent := os.NewFile(uintptr(fds[0]), "openvpn-parent")
	child := os.NewFile(uintptr(fds[1]), "openvpn-child")
	defer child.Close()
	packets, err := net.FileConn(parent)
	_ = parent.Close()
	if err != nil {
		return nil, errors.New("OpenVPN packet socket could not be prepared")
	}
	lifetime, cancel := context.WithCancel(ctx)
	command := exec.CommandContext(lifetime, enginePath, "--config", profilePath, "--tun-fd", "3")
	if username != "" {
		command.Args = append(command.Args, "--username", username)
	}
	command.ExtraFiles = []*os.File{child}
	diagnostics := &openVPNDiagnostics{}
	command.Stderr = diagnostics
	command.WaitDelay = time.Second
	stdin, err := command.StdinPipe()
	if err != nil {
		cancel()
		_ = packets.Close()
		return nil, errors.New("OpenVPN credential channel could not be prepared")
	}
	defer stdin.Close()
	stdout, err := command.StdoutPipe()
	if err != nil {
		cancel()
		_ = packets.Close()
		return nil, errors.New("OpenVPN engine output could not be prepared")
	}
	if err := command.Start(); err != nil {
		cancel()
		_ = packets.Close()
		_ = stdout.Close()
		return nil, errors.New("OpenVPN engine could not start")
	}
	_ = child.Close()
	inputDone := make(chan struct{})
	go func() {
		defer close(inputDone)
		_, _ = stdin.Write(input)
		_ = stdin.Close()
	}()
	// Join the private write before clearing its buffer. CONNECTED implies the
	// child consumed it, but process I/O alone is not Go memory synchronization.
	defer func() { _ = stdin.Close(); <-inputDone }()
	engine := &openVPNEngine{packets: packets, cancel: cancel, done: make(chan struct{}), readDone: make(chan struct{}), diagnostics: diagnostics}
	go func() {
		_ = command.Wait()
		clearBytes(diagnostics.line[:])
		close(engine.done)
	}()
	type readinessResult struct {
		config protocol.NetworkConfiguration
		policy openConnectRoutePolicy
		err    error
	}
	ready := make(chan readinessResult, 1)
	readDone := engine.readDone
	go func() {
		defer close(readDone)
		reader := bufio.NewReaderSize(stdout, 512*1024)
		line, readErr := reader.ReadSlice('\n')
		var result readinessResult
		if readErr != nil {
			result.err = errors.New("OpenVPN engine did not return bounded readiness")
		} else {
			result.config, result.policy, result.err = parseOpenVPNReadiness(line)
		}
		clearBytes(line)
		ready <- result
		_, _ = io.Copy(io.Discard, reader)
	}()
	startup, stopStartup := context.WithTimeout(ctx, 75*time.Second)
	defer stopStartup()
	select {
	case result := <-ready:
		err = result.err
		engine.config, engine.policy = result.config, result.policy
	case <-startup.Done():
		err = errors.New("OpenVPN session did not become ready")
	case <-engine.done:
		err = errors.New("OpenVPN engine stopped during startup")
	}
	if err != nil {
		engine.Close()
		_ = stdout.Close()
		<-readDone
		if diagnostics.authenticationFailed {
			err = errors.New("OpenVPN authentication failed")
		}
		return nil, err
	}
	// Reading continues only to discard private diagnostics. The process owns
	// the pipe, so it closes and this goroutine exits when Close reaps the child.
	return engine, nil
}

// Only the engine's fixed authentication category crosses this boundary. No
// private stderr text is retained or copied into application diagnostics.
type openVPNDiagnostics struct {
	line                 [128]byte
	length               int
	overlong             bool
	authenticationFailed bool
}

func (diagnostics *openVPNDiagnostics) Write(value []byte) (int, error) {
	for _, character := range value {
		if character == '\n' {
			if !diagnostics.overlong && bytes.Equal(diagnostics.line[:diagnostics.length], []byte("ERROR authentication_failed")) {
				diagnostics.authenticationFailed = true
			}
			clearBytes(diagnostics.line[:])
			diagnostics.length, diagnostics.overlong = 0, false
		} else if diagnostics.length < len(diagnostics.line) {
			diagnostics.line[diagnostics.length] = character
			diagnostics.length++
		} else {
			diagnostics.overlong = true
		}
	}
	return len(value), nil
}

type openVPNReadiness struct {
	IPv4     *string   `json:"ipv4_address"`
	IPv6     *string   `json:"ipv6_address"`
	MTU      int       `json:"mtu"`
	DNS      []string  `json:"dns_servers"`
	Routes4  *[]string `json:"ipv4_routes"`
	Routes6  *[]string `json:"ipv6_routes"`
	Excludes []string  `json:"exclude_routes"`
}

func parseOpenVPNReadiness(line []byte) (protocol.NetworkConfiguration, openConnectRoutePolicy, error) {
	config := protocol.NetworkConfiguration{InterfaceName: "gsnet0"}
	policy := openConnectRoutePolicy{}
	fail := func() (protocol.NetworkConfiguration, openConnectRoutePolicy, error) {
		return protocol.NetworkConfiguration{}, openConnectRoutePolicy{}, errors.New("OpenVPN engine readiness is invalid")
	}
	if !bytes.HasPrefix(line, []byte("READY v1 ")) {
		return fail()
	}
	decoder := json.NewDecoder(bytes.NewReader(line[len("READY v1 "):]))
	decoder.DisallowUnknownFields()
	var value openVPNReadiness
	if err := decoder.Decode(&value); err != nil {
		return fail()
	}
	if decoder.Decode(new(any)) != io.EOF || value.MTU < 576 || value.MTU > 65535 {
		return fail()
	}
	for index, text := range []*string{value.IPv4, value.IPv6} {
		if text == nil {
			continue
		}
		prefix, err := netip.ParsePrefix(*text)
		if err != nil || prefix.Bits() == 0 || prefix.Addr().Is4() != (index == 0) || !usableVPNAddress(prefix.Addr()) {
			return fail()
		}
		if index == 0 {
			config.IPv4Address = &prefix
		} else {
			config.IPv6Address = &prefix
		}
	}
	if config.IPv4Address == nil && config.IPv6Address == nil || config.IPv6Address != nil && value.MTU < 1280 || len(value.DNS) > 4 {
		return fail()
	}
	config.MTU = uint16(value.MTU)
	for _, text := range value.DNS {
		address, err := netip.ParseAddr(text)
		if err != nil || !usableVPNAddress(address) || address.Is4() && config.IPv4Address == nil || address.Is6() && config.IPv6Address == nil {
			return fail()
		}
		config.DNSServers = append(config.DNSServers, address)
	}
	ensureVPNDNS(&config)
	policy.vpnDNS = make(map[netip.Addr]struct{}, len(config.DNSServers))
	for _, address := range config.DNSServers {
		policy.vpnDNS[address] = struct{}{}
	}
	for index, routes := range []*[]string{value.Routes4, value.Routes6} {
		if routes == nil {
			continue
		}
		family := &policy.ipv4
		if index == 1 {
			family = &policy.ipv6
		}
		family.includesConfigured = true
		if len(*routes) > maximumOpenConnectRoutes {
			return fail()
		}
		for _, text := range *routes {
			prefix, err := netip.ParsePrefix(text)
			if err != nil || prefix.Addr().Is4() != (index == 0) {
				return fail()
			}
			family.includes = append(family.includes, prefix.Masked())
		}
	}
	if len(value.Excludes) > maximumOpenConnectRoutes {
		return fail()
	}
	for _, text := range value.Excludes {
		prefix, err := netip.ParsePrefix(text)
		if err != nil {
			return fail()
		}
		if prefix.Addr().Is4() {
			policy.ipv4.excludes = append(policy.ipv4.excludes, prefix.Masked())
		} else {
			policy.ipv6.excludes = append(policy.ipv6.excludes, prefix.Masked())
		}
	}
	return config, policy, nil
}

func usableVPNAddress(address netip.Addr) bool {
	return address.IsValid() && address.Zone() == "" && !address.Is4In6() && !address.IsUnspecified() && !address.IsMulticast() && !address.IsLoopback() && !address.IsLinkLocalUnicast()
}
