package relay

import (
	"bytes"
	"encoding/binary"
	"io"
	"testing"
)

func TestFramesRoundTripAndRejectMalformedLengths(t *testing.T) {
	for _, size := range []int{14, 64, maximumFrame} {
		want := bytes.Repeat([]byte{42}, size)
		var wire bytes.Buffer
		if err := writeFrame(&wire, want); err != nil {
			t.Fatal(err)
		}
		got := make([]byte, maximumFrame)
		n, err := readFrame(&wire, got)
		if err != nil || !bytes.Equal(want, got[:n]) {
			t.Fatalf("round trip: %v", err)
		}
	}
	for _, size := range []uint16{0, 13, maximumFrame + 1, 65535} {
		var header [2]byte
		binary.BigEndian.PutUint16(header[:], size)
		if _, err := readFrame(bytes.NewReader(header[:]), make([]byte, maximumFrame)); err == nil {
			t.Fatalf("accepted length %d", size)
		}
	}
	if _, err := readFrame(bytes.NewReader([]byte{0, 14, 1}), make([]byte, maximumFrame)); err != io.ErrUnexpectedEOF {
		t.Fatalf("truncated frame: %v", err)
	}
}
