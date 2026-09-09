package host

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

const validOpenVPNReady = `READY v1 {"ipv4_address":"10.8.0.2/24","ipv6_address":null,"mtu":1500,"dns_servers":["10.8.0.1"],"ipv4_routes":null,"ipv6_routes":[],"exclude_routes":[]}` + "\n"

func TestOpenVPNReadinessFullAndSplitPolicy(t *testing.T) {
	config, policy, err := parseOpenVPNReadiness([]byte(validOpenVPNReady))
	if err != nil || config.IPv4Address.String() != "10.8.0.2/24" || config.MTU != 1500 {
		t.Fatalf("readiness = %v, %v", config, err)
	}
	if !policy.useVPNAddress(netip.MustParseAddr("8.8.8.8")) || policy.useVPNAddress(netip.MustParseAddr("2001:db8::1")) {
		t.Fatal("null/full and empty/no-includes were confused")
	}
	text := strings.Replace(validOpenVPNReady, `"ipv4_routes":null`, `"ipv4_routes":["10.0.0.0/8"]`, 1)
	text = strings.Replace(text, `"exclude_routes":[]`, `"exclude_routes":["10.9.0.0/16","10.8.0.1/32"]`, 1)
	_, policy, err = parseOpenVPNReadiness([]byte(text))
	if err != nil || !policy.useVPNAddress(netip.MustParseAddr("10.1.0.1")) || policy.useVPNAddress(netip.MustParseAddr("10.9.0.1")) || policy.useVPNAddress(netip.MustParseAddr("8.8.8.8")) {
		t.Fatal("split policy did not preserve include/exclude routing", err)
	}
	if !policy.useVPNAddress(netip.MustParseAddr("10.8.0.1")) {
		t.Fatal("VPN DNS must remain inside the VPN despite an exclusion")
	}
	text = strings.Replace(validOpenVPNReady, `["10.8.0.1"]`, `[]`, 1)
	config, policy, err = parseOpenVPNReadiness([]byte(text))
	if err != nil || len(config.DNSServers) == 0 || !policy.useVPNAddress(config.DNSServers[0]) {
		t.Fatal("missing DNS was not supplied inside the VPN", err)
	}
}

func TestOpenVPNReadinessRejectsInvalidMetadata(t *testing.T) {
	for _, text := range []string{
		"ERROR private-password\n",
		strings.Replace(validOpenVPNReady, "1500", "65536", 1),
		strings.Replace(validOpenVPNReady, "10.8.0.2/24", "127.0.0.1/8", 1),
		strings.Replace(validOpenVPNReady, "10.8.0.2/24", "::ffff:10.8.0.2/120", 1),
		strings.Replace(validOpenVPNReady, "10.8.0.1", "::1", 1),
		strings.Replace(validOpenVPNReady, `"ipv4_routes":null`, `"ipv4_routes":["::/0"]`, 1),
		strings.Replace(validOpenVPNReady, `"mtu":1500`, `"secret":"private-password","mtu":1500`, 1),
		validOpenVPNReady + "{}",
	} {
		if _, _, err := parseOpenVPNReadiness([]byte(text)); err == nil || strings.Contains(err.Error(), "private-password") {
			t.Fatalf("invalid metadata accepted or exposed: %v", err)
		}
	}
}

func openVPNFixture(t *testing.T, mode string) string {
	t.Helper()
	t.Setenv("ASURA_TEST_OPENVPN_ENGINE", mode)
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "engine")
	script := "#!/bin/sh\nexec '" + strings.ReplaceAll(executable, "'", "'\\''") + "' -test.run=^TestOpenVPNEngineProcess$ -- \"$@\"\n"
	if err := os.WriteFile(path, []byte(script), 0o700); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestOpenVPNEngineDatagramAndLifecycle(t *testing.T) {
	path := openVPNFixture(t, "ready")
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	engine, err := startOpenVPNEngine(ctx, path, "/private/test.ovpn", "fixture-user", strings.NewReader("fixture-password\n"))
	if err != nil {
		t.Fatal(err)
	}
	defer engine.Close()
	_ = engine.packets.SetDeadline(time.Now().Add(time.Second))
	packet := []byte{0x45, 0, 0, 4}
	if _, err := engine.packets.Write(packet); err != nil {
		t.Fatal(err)
	}
	buffer := make([]byte, 64)
	n, err := engine.packets.Read(buffer)
	if err != nil || !bytes.Equal(packet, buffer[:n]) {
		t.Fatalf("raw datagram changed: %x, %v", buffer[:n], err)
	}
	engine.Close()
	select {
	case <-engine.done:
	default:
		t.Fatal("child not reaped")
	}
	select {
	case <-engine.readDone:
	default:
		t.Fatal("reader not joined")
	}
}

func TestOpenVPNEngineRejectsPrivateOutputAndCancelsStartup(t *testing.T) {
	for _, mode := range []string{"invalid", "oversized", "waiting"} {
		t.Run(mode, func(t *testing.T) {
			path := openVPNFixture(t, mode)
			ctx, cancel := context.WithTimeout(context.Background(), 250*time.Millisecond)
			defer cancel()
			engine, err := startOpenVPNEngine(ctx, path, "/private/test.ovpn", "fixture-user", strings.NewReader("fixture-password"))
			if engine != nil || err == nil || strings.Contains(err.Error(), "private-password") {
				t.Fatalf("bad startup result: %v", err)
			}
		})
	}
}

func TestOpenVPNEngineProjectsOnlyAuthenticationCategory(t *testing.T) {
	path := openVPNFixture(t, "authentication")
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	_, err := startOpenVPNEngine(ctx, path, "/private/test.ovpn", "fixture-user", strings.NewReader("fixture-password"))
	if err == nil || err.Error() != "OpenVPN authentication failed" {
		t.Fatalf("authentication category missing: %v", err)
	}
}

// This process is invoked only by the local test wrapper. It never creates a
// network connection: FD3 is an inherited Unix datagram packet transport.
func TestOpenVPNEngineProcess(t *testing.T) {
	mode := os.Getenv("ASURA_TEST_OPENVPN_ENGINE")
	if mode == "" {
		return
	}
	credential, _ := io.ReadAll(os.Stdin)
	if string(credential) != "fixture-password\n" {
		os.Exit(3)
	}
	clearBytes(credential)
	if mode == "authentication" {
		fmt.Fprintln(os.Stderr, "private-password\nERROR authentication_failed")
		os.Exit(1)
	}
	if mode == "invalid" {
		fmt.Fprintln(os.Stdout, "ERROR private-password")
	}
	if mode == "oversized" {
		fmt.Fprintln(os.Stdout, strings.Repeat("x", 512*1024))
	}
	if mode == "ready" {
		fmt.Fprint(os.Stdout, validOpenVPNReady)
	}
	fmt.Fprintln(os.Stderr, "private-password")
	packet := os.NewFile(3, "packet")
	buffer := make([]byte, 65536)
	for {
		n, err := packet.Read(buffer)
		if err != nil {
			os.Exit(0)
		}
		if _, err = packet.Write(buffer[:n]); err != nil {
			os.Exit(0)
		}
	}
}
