package protocol

import (
	"bytes"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"testing"
)

func TestFrameMatchesVersionOneGoldenBytes(t *testing.T) {
	t.Parallel()
	key := sequentialKey()
	packet := ipv4Packet()
	var encoded bytes.Buffer
	if err := writeFrame(&encoded, key, IPPacket, 42, packet); err != nil {
		t.Fatal(err)
	}

	const expected = "47534e5701040000000000000000002a000000144500001400000000400100007f0000017f0000012c7be3b20780fa809e27afcfe67bdc9f02a5a9a1aabc3e82e4b908a77f04d185"
	if actual := hex.EncodeToString(encoded.Bytes()); actual != expected {
		t.Fatalf("encoded frame mismatch\nwant %s\n got %s", expected, actual)
	}

	decoded, err := readFrame(bytes.NewReader(encoded.Bytes()), key, 42)
	if err != nil {
		t.Fatal(err)
	}
	if decoded.kind != IPPacket || !bytes.Equal(decoded.payload, packet) {
		t.Fatal("decoded frame did not preserve kind and payload")
	}
}

func TestReadFrameRejectsOversizeBeforeReadingPayload(t *testing.T) {
	t.Parallel()
	header := validHeader(IPPacket, 0, MaximumPayloadLength+1)
	_, err := readFrame(bytes.NewReader(header), sequentialKey(), 0)
	if !errors.Is(err, ErrTooLarge) {
		t.Fatalf("expected frame-too-large error, got %v", err)
	}
}

func TestReadFrameRejectsInvalidAuthenticationTag(t *testing.T) {
	t.Parallel()
	var encoded bytes.Buffer
	if err := writeFrame(&encoded, sequentialKey(), IPPacket, 0, ipv4Packet()); err != nil {
		t.Fatal(err)
	}
	value := encoded.Bytes()
	value[len(value)-1] ^= 0xff
	_, err := readFrame(bytes.NewReader(value), sequentialKey(), 0)
	if !errors.Is(err, ErrAuthentication) {
		t.Fatalf("expected authentication error, got %v", err)
	}
}

func TestReadFrameRejectsUnexpectedSequence(t *testing.T) {
	t.Parallel()
	var encoded bytes.Buffer
	if err := writeFrame(&encoded, sequentialKey(), IPPacket, 9, nil); err != nil {
		t.Fatal(err)
	}
	_, err := readFrame(bytes.NewReader(encoded.Bytes()), sequentialKey(), 10)
	if !errors.Is(err, ErrSequence) {
		t.Fatalf("expected sequence error, got %v", err)
	}
}

func TestReadFrameRejectsReservedHeaderBits(t *testing.T) {
	t.Parallel()
	header := validHeader(IPPacket, 0, 0)
	header[6] = 1
	_, err := readFrame(bytes.NewReader(header), sequentialKey(), 0)
	if !errors.Is(err, ErrMalformed) {
		t.Fatalf("expected malformed-frame error, got %v", err)
	}
}

func validHeader(kind MessageKind, sequence uint64, payloadLength int) []byte {
	header := make([]byte, headerLength)
	binary.BigEndian.PutUint32(header[0:4], magic)
	header[4] = Version
	header[5] = byte(kind)
	binary.BigEndian.PutUint64(header[8:16], sequence)
	binary.BigEndian.PutUint32(header[16:20], uint32(payloadLength))
	return header
}

func sequentialKey() []byte {
	key := make([]byte, AuthenticationKeyLength)
	for index := range key {
		key[index] = byte(index)
	}
	return key
}

func ipv4Packet() []byte {
	packet, _ := hex.DecodeString("4500001400000000400100007f0000017f000001")
	return packet
}
