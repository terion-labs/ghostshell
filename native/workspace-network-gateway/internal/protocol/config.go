package protocol

import (
	"encoding/binary"
	"fmt"
	"net/netip"
)

const maximumDNSServers = 4

type NetworkConfiguration struct {
	InterfaceName string
	MTU           uint16
	IPv4Address   *netip.Prefix
	IPv6Address   *netip.Prefix
	DNSServers    []netip.Addr
}

func decodeConfiguration(payload []byte) (NetworkConfiguration, error) {
	decoder := configurationDecoder{payload: payload}
	flags, err := decoder.byte()
	if err != nil || flags == 0 || flags&^byte(3) != 0 {
		return NetworkConfiguration{}, ErrMalformed
	}
	mtuBytes, err := decoder.take(2)
	if err != nil {
		return NetworkConfiguration{}, err
	}
	mtu := binary.BigEndian.Uint16(mtuBytes)
	nameLength, err := decoder.byte()
	if err != nil {
		return NetworkConfiguration{}, err
	}
	nameBytes, err := decoder.take(int(nameLength))
	if err != nil || !validInterfaceName(nameBytes) {
		return NetworkConfiguration{}, ErrMalformed
	}

	configuration := NetworkConfiguration{
		InterfaceName: string(nameBytes),
		MTU:           mtu,
	}
	if flags&1 != 0 {
		prefix, decodeErr := decoder.prefix(4)
		if decodeErr != nil || !prefix.Addr().Is4() || prefix.Bits() < 1 || prefix.Bits() > 32 {
			return NetworkConfiguration{}, ErrMalformed
		}
		configuration.IPv4Address = &prefix
	}
	if flags&2 != 0 {
		prefix, decodeErr := decoder.prefix(16)
		if decodeErr != nil || !prefix.Addr().Is6() || prefix.Bits() < 1 || prefix.Bits() > 128 {
			return NetworkConfiguration{}, ErrMalformed
		}
		configuration.IPv6Address = &prefix
	}

	dnsCount, err := decoder.byte()
	if err != nil || dnsCount < 1 || dnsCount > maximumDNSServers {
		return NetworkConfiguration{}, ErrMalformed
	}
	configuration.DNSServers = make([]netip.Addr, 0, int(dnsCount))
	for dnsIndex := 0; dnsIndex < int(dnsCount); dnsIndex++ {
		family, familyErr := decoder.byte()
		if familyErr != nil {
			return NetworkConfiguration{}, familyErr
		}
		length := 0
		switch family {
		case 4:
			length = 4
		case 6:
			length = 16
		default:
			return NetworkConfiguration{}, ErrMalformed
		}
		addressBytes, addressErr := decoder.take(length)
		if addressErr != nil {
			return NetworkConfiguration{}, addressErr
		}
		address, ok := netip.AddrFromSlice(addressBytes)
		if !ok || (address.Is4() && configuration.IPv4Address == nil) {
			return NetworkConfiguration{}, ErrMalformed
		}
		if address.Is6() && configuration.IPv6Address == nil {
			return NetworkConfiguration{}, ErrMalformed
		}
		configuration.DNSServers = append(configuration.DNSServers, address)
	}
	if decoder.offset != len(payload) {
		return NetworkConfiguration{}, ErrMalformed
	}
	if mtu == 0 || configuration.IPv6Address != nil && mtu < 1280 || configuration.IPv6Address == nil && mtu < 576 {
		return NetworkConfiguration{}, fmt.Errorf("%w: invalid MTU", ErrMalformed)
	}
	return configuration, nil
}

func encodeConfiguration(configuration NetworkConfiguration) ([]byte, error) {
	if !validInterfaceName([]byte(configuration.InterfaceName)) {
		return nil, ErrMalformed
	}
	flags := byte(0)
	length := 1 + 2 + 1 + len(configuration.InterfaceName) + 1
	if configuration.IPv4Address != nil {
		if !configuration.IPv4Address.IsValid() || !configuration.IPv4Address.Addr().Is4() || configuration.IPv4Address.Bits() < 1 {
			return nil, ErrMalformed
		}
		flags |= 1
		length += 5
	}
	if configuration.IPv6Address != nil {
		if !configuration.IPv6Address.IsValid() || !configuration.IPv6Address.Addr().Is6() || configuration.IPv6Address.Bits() < 1 {
			return nil, ErrMalformed
		}
		flags |= 2
		length += 17
	}
	if flags == 0 || configuration.MTU == 0 || configuration.IPv6Address != nil && configuration.MTU < 1280 || configuration.IPv6Address == nil && configuration.MTU < 576 {
		return nil, ErrMalformed
	}
	if len(configuration.DNSServers) < 1 || len(configuration.DNSServers) > maximumDNSServers {
		return nil, ErrMalformed
	}
	for _, server := range configuration.DNSServers {
		if server.Is4() && configuration.IPv4Address != nil {
			length += 5
		} else if server.Is6() && configuration.IPv6Address != nil {
			length += 17
		} else {
			return nil, ErrMalformed
		}
	}

	value := make([]byte, 0, length)
	value = append(value, flags, byte(configuration.MTU>>8), byte(configuration.MTU), byte(len(configuration.InterfaceName)))
	value = append(value, configuration.InterfaceName...)
	if configuration.IPv4Address != nil {
		value = append(value, configuration.IPv4Address.Addr().AsSlice()...)
		value = append(value, byte(configuration.IPv4Address.Bits()))
	}
	if configuration.IPv6Address != nil {
		value = append(value, configuration.IPv6Address.Addr().AsSlice()...)
		value = append(value, byte(configuration.IPv6Address.Bits()))
	}
	value = append(value, byte(len(configuration.DNSServers)))
	for _, server := range configuration.DNSServers {
		if server.Is4() {
			value = append(value, 4)
		} else {
			value = append(value, 6)
		}
		value = append(value, server.AsSlice()...)
	}
	return value, nil
}

type configurationDecoder struct {
	payload []byte
	offset  int
}

func (decoder *configurationDecoder) byte() (byte, error) {
	value, err := decoder.take(1)
	if err != nil {
		return 0, err
	}
	return value[0], nil
}

func (decoder *configurationDecoder) take(length int) ([]byte, error) {
	if length < 0 || decoder.offset > len(decoder.payload)-length {
		return nil, ErrMalformed
	}
	value := decoder.payload[decoder.offset : decoder.offset+length]
	decoder.offset += length
	return value, nil
}

func (decoder *configurationDecoder) prefix(addressLength int) (netip.Prefix, error) {
	addressBytes, err := decoder.take(addressLength)
	if err != nil {
		return netip.Prefix{}, err
	}
	prefixLength, err := decoder.byte()
	if err != nil {
		return netip.Prefix{}, err
	}
	address, ok := netip.AddrFromSlice(addressBytes)
	if !ok {
		return netip.Prefix{}, ErrMalformed
	}
	return netip.PrefixFrom(address, int(prefixLength)), nil
}

func validInterfaceName(name []byte) bool {
	if len(name) < 1 || len(name) > 15 || string(name) == "." || string(name) == ".." {
		return false
	}
	for _, character := range name {
		if character >= 'a' && character <= 'z' ||
			character >= 'A' && character <= 'Z' ||
			character >= '0' && character <= '9' ||
			character == '.' || character == '_' || character == '-' {
			continue
		}
		return false
	}
	return true
}
