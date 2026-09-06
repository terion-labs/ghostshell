package protocol

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
)

const (
	Version                 = byte(1)
	AuthenticationKeyLength = sha256.Size
	MaximumPayloadLength    = 65_535
	headerLength            = 20
	tagLength               = sha256.Size
	magic                   = uint32(0x47534e57)
)

type MessageKind byte

const (
	GuestHello        MessageKind = 1
	HostConfiguration MessageKind = 2
	GuestReady        MessageKind = 3
	IPPacket          MessageKind = 4
)

var (
	ErrAuthentication = errors.New("packet channel authentication failed")
	ErrMalformed      = errors.New("packet channel frame is malformed")
	ErrSequence       = errors.New("packet channel sequence is invalid")
	ErrTooLarge       = errors.New("packet channel frame is too large")
	ErrVersion        = errors.New("packet channel version is unsupported")
)

type frame struct {
	kind    MessageKind
	payload []byte
}

func writeFrame(writer io.Writer, key []byte, kind MessageKind, sequence uint64, payload []byte) error {
	if len(key) != AuthenticationKeyLength {
		return fmt.Errorf("authentication key must contain %d bytes", AuthenticationKeyLength)
	}
	if len(payload) > MaximumPayloadLength {
		return ErrTooLarge
	}

	header := make([]byte, headerLength)
	binary.BigEndian.PutUint32(header[0:4], magic)
	header[4] = Version
	header[5] = byte(kind)
	binary.BigEndian.PutUint64(header[8:16], sequence)
	binary.BigEndian.PutUint32(header[16:20], uint32(len(payload)))
	tag := authenticate(key, header, payload)
	defer clearBytes(tag)

	if err := writeAll(writer, header); err != nil {
		return err
	}
	if err := writeAll(writer, payload); err != nil {
		return err
	}
	return writeAll(writer, tag)
}

func readFrame(reader io.Reader, key []byte, expectedSequence uint64) (frame, error) {
	if len(key) != AuthenticationKeyLength {
		return frame{}, fmt.Errorf("authentication key must contain %d bytes", AuthenticationKeyLength)
	}

	header := make([]byte, headerLength)
	if _, err := io.ReadFull(reader, header); err != nil {
		return frame{}, fmt.Errorf("read packet frame header: %w", err)
	}
	if binary.BigEndian.Uint32(header[0:4]) != magic || header[6] != 0 || header[7] != 0 {
		return frame{}, ErrMalformed
	}
	if header[4] != Version {
		return frame{}, ErrVersion
	}
	kind := MessageKind(header[5])
	if kind < GuestHello || kind > IPPacket {
		return frame{}, ErrMalformed
	}
	if binary.BigEndian.Uint64(header[8:16]) != expectedSequence {
		return frame{}, ErrSequence
	}
	payloadLength := binary.BigEndian.Uint32(header[16:20])
	if payloadLength > MaximumPayloadLength {
		return frame{}, ErrTooLarge
	}

	payload := make([]byte, int(payloadLength))
	if _, err := io.ReadFull(reader, payload); err != nil {
		clearBytes(payload)
		return frame{}, fmt.Errorf("read packet frame payload: %w", err)
	}
	suppliedTag := make([]byte, tagLength)
	defer clearBytes(suppliedTag)
	if _, err := io.ReadFull(reader, suppliedTag); err != nil {
		clearBytes(payload)
		return frame{}, fmt.Errorf("read packet frame tag: %w", err)
	}
	expectedTag := authenticate(key, header, payload)
	defer clearBytes(expectedTag)
	if !hmac.Equal(expectedTag, suppliedTag) {
		clearBytes(payload)
		return frame{}, ErrAuthentication
	}
	return frame{kind: kind, payload: payload}, nil
}

func authenticate(key, header, payload []byte) []byte {
	hash := hmac.New(sha256.New, key)
	_, _ = hash.Write(header)
	_, _ = hash.Write(payload)
	return hash.Sum(nil)
}

func writeAll(writer io.Writer, value []byte) error {
	for len(value) > 0 {
		written, err := writer.Write(value)
		if err != nil {
			return err
		}
		if written == 0 {
			return io.ErrShortWrite
		}
		value = value[written:]
	}
	return nil
}

func clearBytes(value []byte) {
	for index := range value {
		value[index] = 0
	}
}
