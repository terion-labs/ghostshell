package protocol

import (
	"errors"
	"testing"
)

func TestDecodeConfigurationRejectsMalformedInputs(t *testing.T) {
	t.Parallel()
	valid := encodeTestConfiguration()
	tests := map[string][]byte{
		"empty":               nil,
		"unknown flags":       replacingByte(valid, 0, 4),
		"zero MTU":            replacingByte(replacingByte(valid, 1, 0), 2, 0),
		"unsafe interface":    replacingByte(valid, 4, '/'),
		"truncated":           append([]byte{}, valid[:len(valid)-1]...),
		"trailing data":       append(append([]byte{}, valid...), 0),
		"unsupported DNS":     replacingByte(valid, 33, 5),
		"IPv6 DNS without v6": ipv6DNSWithoutIPv6(valid),
	}
	for name, input := range tests {
		name, input := name, input
		t.Run(name, func(t *testing.T) {
			t.Parallel()
			if _, err := decodeConfiguration(input); !errors.Is(err, ErrMalformed) {
				t.Fatalf("expected malformed configuration, got %v", err)
			}
		})
	}
}

func TestConfigurationEncodingRoundTrips(t *testing.T) {
	t.Parallel()
	decoded, err := decodeConfiguration(encodeTestConfiguration())
	if err != nil {
		t.Fatal(err)
	}
	encoded, err := encodeConfiguration(decoded)
	if err != nil {
		t.Fatal(err)
	}
	decodedAgain, err := decodeConfiguration(encoded)
	if err != nil {
		t.Fatal(err)
	}
	if decodedAgain.InterfaceName != decoded.InterfaceName || decodedAgain.MTU != decoded.MTU ||
		decodedAgain.IPv4Address.String() != decoded.IPv4Address.String() ||
		decodedAgain.IPv6Address.String() != decoded.IPv6Address.String() {
		t.Fatalf("configuration did not round-trip: %#v", decodedAgain)
	}
}

func replacingByte(value []byte, index int, replacement byte) []byte {
	copyOfValue := append([]byte{}, value...)
	copyOfValue[index] = replacement
	return copyOfValue
}

func ipv6DNSWithoutIPv6(valid []byte) []byte {
	// flags + MTU + name + IPv4 prefix, followed by one IPv6 DNS server.
	value := append([]byte{}, valid[:15]...)
	value[0] = 1
	value = append(value, 1, 6)
	value = append(value, valid[len(valid)-16:]...)
	return value
}
