// Package relay carries Ethernet frames over a container exec stream. Routing
// stays in the host Ethernet gateway; this transport has no upstream sockets.
package relay

import (
	"encoding/binary"
	"errors"
	"io"
)

const maximumFrame = 1514

func readFrame(r io.Reader, frame []byte) (int, error) {
	var size [2]byte
	if _, err := io.ReadFull(r, size[:]); err != nil {
		return 0, err
	}
	n := int(binary.BigEndian.Uint16(size[:]))
	if n < 14 || n > maximumFrame || n > len(frame) {
		return 0, errors.New("invalid relay Ethernet frame length")
	}
	return io.ReadFull(r, frame[:n])
}

func writeFrame(w io.Writer, frame []byte) error {
	if len(frame) < 14 || len(frame) > maximumFrame {
		return errors.New("invalid relay Ethernet frame length")
	}
	var size [2]byte
	binary.BigEndian.PutUint16(size[:], uint16(len(frame)))
	for _, part := range [][]byte{size[:], frame} {
		n, err := w.Write(part)
		if err != nil {
			return err
		}
		if n != len(part) {
			return io.ErrShortWrite
		}
	}
	return nil
}
