//go:build linux

package guest

import (
	"net"
	"os"
	"path/filepath"
	"strconv"
	"testing"
)

func TestClaimPidFileIsExclusiveAndReleaseIsOwnershipChecked(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "network.pid")
	owned, err := claimPidFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := claimPidFile(path); err == nil {
		t.Fatal("expected a second owner to be rejected")
	}
	if err := os.WriteFile(path, []byte("2\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	owned.release()
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("release removed a PID file owned by another process: %v", err)
	}
}

func TestStopWithoutPidRemovesOnlyAStaleSocket(t *testing.T) {
	t.Parallel()
	directory := t.TempDir()
	socketPath := filepath.Join(directory, "network.sock")
	listener, err := net.Listen("unix", socketPath)
	if err != nil {
		t.Fatal(err)
	}
	if err := listener.Close(); err != nil {
		t.Fatal(err)
	}

	if err := stopOwnedGuest(filepath.Join(directory, "network.pid"), socketPath); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Lstat(socketPath); !os.IsNotExist(err) {
		t.Fatalf("expected stale socket to be removed, got %v", err)
	}
}

func TestOwnedGuestProcessRequiresGuestAndExactOwnershipPaths(t *testing.T) {
	t.Parallel()
	pidFile := "/run/ghostshell/network.pid"
	socket := "/run/ghostshell/network.sock"
	valid := []byte("/opt/ghostshell/bin/workspace-gateway\x00guest\x00--socket\x00" +
		socket + "\x00--pid-file\x00" + pidFile + "\x00--gateway\x00192.168.64.1\x00")
	if !ownsGuestProcess(valid, pidFile, socket) {
		t.Fatal("expected exact guest ownership arguments to match")
	}
	if ownsGuestProcess(valid, pidFile, socket+".other") {
		t.Fatal("expected a different socket path to be rejected")
	}
	if ownsGuestProcess([]byte(strconv.Itoa(os.Getpid())), pidFile, socket) {
		t.Fatal("expected an unrelated command line to be rejected")
	}
}
