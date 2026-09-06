package protocol

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"fmt"
	"io"
	"sync"
)

var (
	guestToHostLabel = []byte("GhostSHELL workspace packet channel guest to host v1")
	hostToGuestLabel = []byte("GhostSHELL workspace packet channel host to guest v1")
)

type Channel struct {
	transport    io.ReadWriter
	readKey      []byte
	writeKey     []byte
	nextRead     uint64
	nextWrite    uint64
	readMutex    sync.Mutex
	writeMutex   sync.Mutex
	disposeMutex sync.Mutex
	disposed     bool
}

func ConnectGuest(transport io.ReadWriter, authenticationKey []byte, configure func(NetworkConfiguration) error) (*Channel, error) {
	if transport == nil {
		return nil, errors.New("packet channel transport is required")
	}
	if len(authenticationKey) != AuthenticationKeyLength {
		return nil, fmt.Errorf("authentication key must contain %d bytes", AuthenticationKeyLength)
	}
	if configure == nil {
		return nil, errors.New("packet channel configure callback is required")
	}

	guestToHostHandshakeKey := deriveKey(authenticationKey, guestToHostLabel)
	hostToGuestHandshakeKey := deriveKey(authenticationKey, hostToGuestLabel)
	defer clearBytes(guestToHostHandshakeKey)
	defer clearBytes(hostToGuestHandshakeKey)

	guestNonce := make([]byte, sha256.Size)
	if _, err := rand.Read(guestNonce); err != nil {
		return nil, fmt.Errorf("create guest nonce: %w", err)
	}
	defer clearBytes(guestNonce)
	hello := make([]byte, 2+sha256.Size)
	hello[0], hello[1] = Version, Version
	copy(hello[2:], guestNonce)
	if err := writeFrame(transport, guestToHostHandshakeKey, GuestHello, 0, hello); err != nil {
		return nil, fmt.Errorf("send guest hello: %w", err)
	}

	response, err := readFrame(transport, hostToGuestHandshakeKey, 0)
	if err != nil {
		return nil, fmt.Errorf("receive host configuration: %w", err)
	}
	defer clearBytes(response.payload)
	if response.kind != HostConfiguration || len(response.payload) <= 1+2*sha256.Size || response.payload[0] != Version {
		return nil, ErrMalformed
	}
	if !hmac.Equal(response.payload[1:1+sha256.Size], guestNonce) {
		return nil, ErrAuthentication
	}
	hostNonce := append([]byte(nil), response.payload[1+sha256.Size:1+2*sha256.Size]...)
	defer clearBytes(hostNonce)
	configuration, err := decodeConfiguration(response.payload[1+2*sha256.Size:])
	if err != nil {
		return nil, err
	}
	if err := configure(configuration); err != nil {
		return nil, fmt.Errorf("configure guest network: %w", err)
	}

	ready := make([]byte, 1+2*sha256.Size)
	defer clearBytes(ready)
	ready[0] = Version
	copy(ready[1:1+sha256.Size], guestNonce)
	copy(ready[1+sha256.Size:], hostNonce)
	if err := writeFrame(transport, guestToHostHandshakeKey, GuestReady, 1, ready); err != nil {
		return nil, fmt.Errorf("send guest ready: %w", err)
	}

	return &Channel{
		transport: transport,
		readKey:   deriveSessionKey(authenticationKey, hostToGuestLabel, guestNonce, hostNonce),
		writeKey:  deriveSessionKey(authenticationKey, guestToHostLabel, guestNonce, hostNonce),
		nextRead:  1,
		nextWrite: 2,
	}, nil
}

func ConnectHost(transport io.ReadWriter, authenticationKey []byte, configuration NetworkConfiguration) (*Channel, error) {
	if transport == nil {
		return nil, errors.New("packet channel transport is required")
	}
	if len(authenticationKey) != AuthenticationKeyLength {
		return nil, fmt.Errorf("authentication key must contain %d bytes", AuthenticationKeyLength)
	}
	encodedConfiguration, err := encodeConfiguration(configuration)
	if err != nil {
		return nil, fmt.Errorf("encode guest network configuration: %w", err)
	}
	defer clearBytes(encodedConfiguration)

	guestToHostHandshakeKey := deriveKey(authenticationKey, guestToHostLabel)
	hostToGuestHandshakeKey := deriveKey(authenticationKey, hostToGuestLabel)
	defer clearBytes(guestToHostHandshakeKey)
	defer clearBytes(hostToGuestHandshakeKey)

	hello, err := readFrame(transport, guestToHostHandshakeKey, 0)
	if err != nil {
		return nil, fmt.Errorf("receive guest hello: %w", err)
	}
	defer clearBytes(hello.payload)
	if hello.kind != GuestHello || len(hello.payload) != 2+sha256.Size || hello.payload[0] > Version || hello.payload[1] < Version {
		return nil, ErrVersion
	}
	guestNonce := append([]byte(nil), hello.payload[2:]...)
	defer clearBytes(guestNonce)
	hostNonce := make([]byte, sha256.Size)
	if _, err := rand.Read(hostNonce); err != nil {
		return nil, fmt.Errorf("create host nonce: %w", err)
	}
	defer clearBytes(hostNonce)

	configurationPayload := make([]byte, 1+2*sha256.Size+len(encodedConfiguration))
	defer clearBytes(configurationPayload)
	configurationPayload[0] = Version
	copy(configurationPayload[1:1+sha256.Size], guestNonce)
	copy(configurationPayload[1+sha256.Size:1+2*sha256.Size], hostNonce)
	copy(configurationPayload[1+2*sha256.Size:], encodedConfiguration)
	if err := writeFrame(transport, hostToGuestHandshakeKey, HostConfiguration, 0, configurationPayload); err != nil {
		return nil, fmt.Errorf("send host configuration: %w", err)
	}

	ready, err := readFrame(transport, guestToHostHandshakeKey, 1)
	if err != nil {
		return nil, fmt.Errorf("receive guest readiness: %w", err)
	}
	defer clearBytes(ready.payload)
	if ready.kind != GuestReady || len(ready.payload) != 1+2*sha256.Size || ready.payload[0] != Version ||
		!hmac.Equal(ready.payload[1:1+sha256.Size], guestNonce) ||
		!hmac.Equal(ready.payload[1+sha256.Size:], hostNonce) {
		return nil, ErrAuthentication
	}

	return &Channel{
		transport: transport,
		readKey:   deriveSessionKey(authenticationKey, guestToHostLabel, guestNonce, hostNonce),
		writeKey:  deriveSessionKey(authenticationKey, hostToGuestLabel, guestNonce, hostNonce),
		nextRead:  2,
		nextWrite: 1,
	}, nil
}

func (channel *Channel) SendPacket(packet []byte) error {
	if err := validateIPPacket(packet); err != nil {
		return err
	}
	channel.writeMutex.Lock()
	defer channel.writeMutex.Unlock()
	if channel.isDisposed() {
		return io.ErrClosedPipe
	}
	if err := writeFrame(channel.transport, channel.writeKey, IPPacket, channel.nextWrite, packet); err != nil {
		return err
	}
	channel.nextWrite++
	return nil
}

func (channel *Channel) ReceivePacket() ([]byte, error) {
	channel.readMutex.Lock()
	defer channel.readMutex.Unlock()
	if channel.isDisposed() {
		return nil, io.ErrClosedPipe
	}
	value, err := readFrame(channel.transport, channel.readKey, channel.nextRead)
	if err != nil {
		return nil, err
	}
	if value.kind != IPPacket {
		clearBytes(value.payload)
		return nil, ErrMalformed
	}
	if err := validateIPPacket(value.payload); err != nil {
		clearBytes(value.payload)
		return nil, err
	}
	channel.nextRead++
	return value.payload, nil
}

func (channel *Channel) Close() {
	_ = channel.Interrupt()
	channel.readMutex.Lock()
	defer channel.readMutex.Unlock()
	channel.writeMutex.Lock()
	defer channel.writeMutex.Unlock()
	channel.disposeMutex.Lock()
	defer channel.disposeMutex.Unlock()
	if channel.disposed {
		return
	}
	channel.disposed = true
	clearBytes(channel.readKey)
	clearBytes(channel.writeKey)
}

// Interrupt closes the underlying transport when it is closable, unblocking a
// concurrent read or write before Close erases the channel keys.
func (channel *Channel) Interrupt() error {
	closer, ok := channel.transport.(io.Closer)
	if !ok {
		return nil
	}
	return closer.Close()
}

func (channel *Channel) isDisposed() bool {
	channel.disposeMutex.Lock()
	defer channel.disposeMutex.Unlock()
	return channel.disposed
}

func deriveKey(authenticationKey, label []byte) []byte {
	return authenticate(authenticationKey, label, nil)
}

func deriveSessionKey(authenticationKey, label, guestNonce, hostNonce []byte) []byte {
	hash := hmac.New(sha256.New, authenticationKey)
	_, _ = hash.Write(label)
	_, _ = hash.Write(guestNonce)
	_, _ = hash.Write(hostNonce)
	return hash.Sum(nil)
}

func validateIPPacket(packet []byte) error {
	if len(packet) == 0 || len(packet) > MaximumPayloadLength {
		return ErrMalformed
	}
	switch packet[0] >> 4 {
	case 4:
		if len(packet) < 20 {
			return ErrMalformed
		}
		headerLength := int(packet[0]&15) * 4
		totalLength := int(packet[2])<<8 | int(packet[3])
		if headerLength < 20 || headerLength > len(packet) || totalLength != len(packet) {
			return ErrMalformed
		}
	case 6:
		if len(packet) < 40 {
			return ErrMalformed
		}
		payloadLength := int(packet[4])<<8 | int(packet[5])
		if payloadLength != len(packet)-40 {
			return ErrMalformed
		}
	default:
		return ErrMalformed
	}
	return nil
}
