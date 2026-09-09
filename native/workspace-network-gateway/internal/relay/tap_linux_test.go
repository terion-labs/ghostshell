//go:build linux

package relay

import (
	"bytes"
	"context"
	"errors"
	"testing"
	"time"

	"golang.org/x/sys/unix"
)

func TestTapIOTransfersPacketsAndCancelsIdleRead(t *testing.T) {
	pair, err := unix.Socketpair(unix.AF_UNIX, unix.SOCK_DGRAM|unix.SOCK_NONBLOCK, 0)
	if err != nil {
		t.Fatal(err)
	}
	defer unix.Close(pair[0])
	defer unix.Close(pair[1])
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	want := []byte("packet")
	if _, err := tapIO(ctx, pair[0], want, true); err != nil {
		t.Fatal(err)
	}
	got := make([]byte, 64)
	n, err := tapIO(ctx, pair[1], got, false)
	if err != nil || !bytes.Equal(want, got[:n]) {
		t.Fatalf("transfer: %v", err)
	}
	idle, stop := context.WithTimeout(ctx, 20*time.Millisecond)
	defer stop()
	start := time.Now()
	_, err = tapIO(idle, pair[1], got, false)
	if !errors.Is(err, context.DeadlineExceeded) || time.Since(start) > 500*time.Millisecond {
		t.Fatalf("idle read: %v", err)
	}
}
