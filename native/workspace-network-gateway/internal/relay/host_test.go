package relay

import (
	"context"
	"io"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestHostRejectsMissingExecAndInvalidReadiness(t *testing.T) {
	for _, command := range [][]string{
		nil,
		{"/does-not-exist"},
		{"/bin/sh", "-c", "printf 'INVALID hello!\\n'"},
	} {
		ctx, cancel := context.WithTimeout(context.Background(), time.Second)
		err := RunHost(ctx, filepath.Join(t.TempDir(), "packets.sock"), command, strings.NewReader(""), io.Discard)
		cancel()
		if err == nil {
			t.Fatal("accepted an invalid relay process")
		}
	}
}

func TestHostCancellationInterruptsReadinessWait(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	start := time.Now()
	err := RunHost(ctx, filepath.Join(t.TempDir(), "packets.sock"), []string{"/bin/sh", "-c", "exec sleep 30"}, strings.NewReader(""), io.Discard)
	if err == nil || time.Since(start) > 3*time.Second {
		t.Fatalf("readiness cancellation failed: %v", err)
	}
}
