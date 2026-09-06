package protocol

import (
	"bytes"
	"crypto/sha256"
	"net"
	"net/netip"
	"testing"
	"time"
)

func TestGuestHandshakeAndPacketSequencesMatchVersionOneHost(t *testing.T) {
	key := sequentialKey()
	guestTransport, hostTransport := net.Pipe()
	defer guestTransport.Close()
	defer hostTransport.Close()
	deadline := time.Now().Add(5 * time.Second)
	_ = guestTransport.SetDeadline(deadline)
	_ = hostTransport.SetDeadline(deadline)

	hostFailure := make(chan error, 1)
	go func() {
		hostFailure <- runTestHost(hostTransport, key)
	}()

	configured := false
	channel, err := ConnectGuest(
		guestTransport,
		key,
		func(configuration NetworkConfiguration) error {
			configured = true
			if configuration.InterfaceName != "gsnet0" || configuration.MTU != 1280 {
				t.Fatalf("unexpected configuration: %#v", configuration)
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	defer channel.Close()
	if !configured {
		t.Fatal("guest network was not configured before readiness")
	}

	received, err := channel.ReceivePacket()
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(received, ipv4Packet()) {
		t.Fatal("guest did not receive host packet")
	}
	if err := channel.SendPacket(ipv4Packet()); err != nil {
		t.Fatal(err)
	}
	if err := <-hostFailure; err != nil {
		t.Fatal(err)
	}
}

func TestGuestHandshakeRejectsHostWithWrongAuthenticationKey(t *testing.T) {
	key := sequentialKey()
	guestTransport, hostTransport := net.Pipe()
	defer guestTransport.Close()
	defer hostTransport.Close()
	deadline := time.Now().Add(5 * time.Second)
	_ = guestTransport.SetDeadline(deadline)
	_ = hostTransport.SetDeadline(deadline)

	go func() {
		guestToHostKey := deriveKey(key, guestToHostLabel)
		hello, err := readFrame(hostTransport, guestToHostKey, 0)
		if err != nil {
			return
		}
		wrongKey := bytes.Repeat([]byte{0xff}, AuthenticationKeyLength)
		hostToGuestKey := deriveKey(wrongKey, hostToGuestLabel)
		payload := hostConfigurationPayload(hello.payload[2:], bytes.Repeat([]byte{7}, sha256.Size))
		_ = writeFrame(hostTransport, hostToGuestKey, HostConfiguration, 0, payload)
	}()

	called := false
	_, err := ConnectGuest(guestTransport, key, func(NetworkConfiguration) error {
		called = true
		return nil
	})
	if err == nil || !bytes.Contains([]byte(err.Error()), []byte(ErrAuthentication.Error())) {
		t.Fatalf("expected authentication failure, got %v", err)
	}
	if called {
		t.Fatal("unauthenticated host configuration reached the network callback")
	}
}

func TestHostAndGuestChannelsInteroperate(t *testing.T) {
	key := sequentialKey()
	guestTransport, hostTransport := net.Pipe()
	defer guestTransport.Close()
	defer hostTransport.Close()
	deadline := time.Now().Add(5 * time.Second)
	_ = guestTransport.SetDeadline(deadline)
	_ = hostTransport.SetDeadline(deadline)

	configuration, err := decodeConfiguration(encodeTestConfiguration())
	if err != nil {
		t.Fatal(err)
	}
	guestResult := make(chan *Channel, 1)
	guestFailure := make(chan error, 1)
	go func() {
		guest, connectErr := ConnectGuest(guestTransport, key, func(received NetworkConfiguration) error {
			if received.InterfaceName != configuration.InterfaceName || received.MTU != configuration.MTU {
				return ErrMalformed
			}
			return nil
		})
		guestResult <- guest
		guestFailure <- connectErr
	}()
	host, err := ConnectHost(hostTransport, key, configuration)
	if err != nil {
		t.Fatal(err)
	}
	defer host.Close()
	guest := <-guestResult
	if err := <-guestFailure; err != nil {
		t.Fatal(err)
	}
	defer guest.Close()

	hostReceived := make(chan []byte, 1)
	hostReceiveFailure := make(chan error, 1)
	go func() {
		packet, receiveErr := host.ReceivePacket()
		hostReceived <- packet
		hostReceiveFailure <- receiveErr
	}()
	if err := guest.SendPacket(ipv4Packet()); err != nil {
		t.Fatal(err)
	}
	if err := <-hostReceiveFailure; err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(<-hostReceived, ipv4Packet()) {
		t.Fatal("host did not receive the complete guest packet")
	}

	guestReceived := make(chan []byte, 1)
	guestReceiveFailure := make(chan error, 1)
	go func() {
		packet, receiveErr := guest.ReceivePacket()
		guestReceived <- packet
		guestReceiveFailure <- receiveErr
	}()
	if err := host.SendPacket(ipv4Packet()); err != nil {
		t.Fatal(err)
	}
	if err := <-guestReceiveFailure; err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(<-guestReceived, ipv4Packet()) {
		t.Fatal("guest did not receive the complete host packet")
	}
}

func runTestHost(transport net.Conn, key []byte) error {
	guestToHostHandshakeKey := deriveKey(key, guestToHostLabel)
	hostToGuestHandshakeKey := deriveKey(key, hostToGuestLabel)
	hello, err := readFrame(transport, guestToHostHandshakeKey, 0)
	if err != nil {
		return err
	}
	hostNonce := bytes.Repeat([]byte{7}, sha256.Size)
	if err := writeFrame(
		transport,
		hostToGuestHandshakeKey,
		HostConfiguration,
		0,
		hostConfigurationPayload(hello.payload[2:], hostNonce)); err != nil {
		return err
	}
	ready, err := readFrame(transport, guestToHostHandshakeKey, 1)
	if err != nil {
		return err
	}
	if ready.kind != GuestReady || !bytes.Equal(ready.payload[1:1+sha256.Size], hello.payload[2:]) {
		return ErrMalformed
	}
	hostToGuestSessionKey := deriveSessionKey(key, hostToGuestLabel, hello.payload[2:], hostNonce)
	guestToHostSessionKey := deriveSessionKey(key, guestToHostLabel, hello.payload[2:], hostNonce)
	if err := writeFrame(transport, hostToGuestSessionKey, IPPacket, 1, ipv4Packet()); err != nil {
		return err
	}
	packet, err := readFrame(transport, guestToHostSessionKey, 2)
	if err != nil {
		return err
	}
	if packet.kind != IPPacket || !bytes.Equal(packet.payload, ipv4Packet()) {
		return ErrMalformed
	}
	return nil
}

func hostConfigurationPayload(guestNonce, hostNonce []byte) []byte {
	encoded := encodeTestConfiguration()
	payload := make([]byte, 1+2*sha256.Size+len(encoded))
	payload[0] = Version
	copy(payload[1:1+sha256.Size], guestNonce)
	copy(payload[1+sha256.Size:1+2*sha256.Size], hostNonce)
	copy(payload[1+2*sha256.Size:], encoded)
	return payload
}

func encodeTestConfiguration() []byte {
	ipv4 := netip.MustParseAddr("100.64.0.2").AsSlice()
	ipv6 := netip.MustParseAddr("fd00::2").AsSlice()
	dns4 := netip.MustParseAddr("1.1.1.1").AsSlice()
	dns6 := netip.MustParseAddr("2606:4700:4700::1111").AsSlice()
	name := []byte("gsnet0")
	value := []byte{3, 5, 0, byte(len(name))}
	value = append(value, name...)
	value = append(value, ipv4...)
	value = append(value, 30)
	value = append(value, ipv6...)
	value = append(value, 126)
	value = append(value, 2, 4)
	value = append(value, dns4...)
	value = append(value, 6)
	value = append(value, dns6...)
	return value
}
