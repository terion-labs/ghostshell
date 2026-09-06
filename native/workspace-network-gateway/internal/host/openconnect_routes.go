package host

import (
	"encoding/binary"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"strconv"
	"strings"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

const maximumOpenConnectRoutes = 4096

type openConnectRouteFamily struct {
	includesConfigured bool
	includes           []netip.Prefix
	excludes           []netip.Prefix
}

type openConnectRoutePolicy struct {
	ipv4   openConnectRouteFamily
	ipv6   openConnectRouteFamily
	vpnDNS map[netip.Addr]struct{}
}

func parseOpenConnectRoutePolicy(
	environment func(string) string,
	configuration protocol.NetworkConfiguration,
) (openConnectRoutePolicy, error) {
	policy := openConnectRoutePolicy{vpnDNS: make(map[netip.Addr]struct{}, len(configuration.DNSServers))}
	for _, server := range configuration.DNSServers {
		policy.vpnDNS[server.Unmap()] = struct{}{}
	}

	var err error
	policy.ipv4, err = parseOpenConnectRouteFamily(environment, "CISCO_SPLIT_INC", "CISCO_SPLIT_EXC", true)
	if err != nil {
		return openConnectRoutePolicy{}, err
	}
	policy.ipv6, err = parseOpenConnectRouteFamily(environment, "CISCO_IPV6_SPLIT_INC", "CISCO_IPV6_SPLIT_EXC", false)
	if err != nil {
		return openConnectRoutePolicy{}, err
	}
	if configuration.IPv4Address == nil && (policy.ipv4.includesConfigured || len(policy.ipv4.excludes) != 0) {
		return openConnectRoutePolicy{}, errors.New("OpenConnect supplied IPv4 split routes without an IPv4 tunnel address")
	}
	if configuration.IPv6Address == nil && (policy.ipv6.includesConfigured || len(policy.ipv6.excludes) != 0) {
		return openConnectRoutePolicy{}, errors.New("OpenConnect supplied IPv6 split routes without an IPv6 tunnel address")
	}
	return policy, nil
}

func parseOpenConnectRouteFamily(
	environment func(string) string,
	includeName string,
	excludeName string,
	ipv4 bool,
) (openConnectRouteFamily, error) {
	includeCount, includesConfigured, err := openConnectRouteCount(environment(includeName), includeName)
	if err != nil {
		return openConnectRouteFamily{}, err
	}
	excludeCount, excludesConfigured, err := openConnectRouteCount(environment(excludeName), excludeName)
	if err != nil {
		return openConnectRouteFamily{}, err
	}
	result := openConnectRouteFamily{includesConfigured: includesConfigured}
	if includesConfigured {
		result.includes, err = parseOpenConnectPrefixes(environment, includeName, includeCount, ipv4)
		if err != nil {
			return openConnectRouteFamily{}, err
		}
	}
	if excludesConfigured {
		result.excludes, err = parseOpenConnectPrefixes(environment, excludeName, excludeCount, ipv4)
		if err != nil {
			return openConnectRouteFamily{}, err
		}
	}
	return result, nil
}

func openConnectRouteCount(text, name string) (int, bool, error) {
	if text == "" {
		return 0, false, nil
	}
	value, err := strconv.Atoi(text)
	if err != nil || value < 0 || value > maximumOpenConnectRoutes {
		return 0, false, fmt.Errorf("OpenConnect supplied an invalid %s route count", name)
	}
	return value, true, nil
}

func parseOpenConnectPrefixes(environment func(string) string, name string, count int, ipv4 bool) ([]netip.Prefix, error) {
	result := make([]netip.Prefix, 0, count)
	for index := 0; index < count; index++ {
		base := fmt.Sprintf("%s_%d", name, index)
		if ipv4 {
			if err := validateOpenConnectRouteSelectors(environment, base); err != nil {
				return nil, err
			}
		}
		prefix, err := parseOpenConnectRoutePrefix(
			environment(base+"_ADDR"),
			environment(base+"_MASKLEN"),
			environment(base+"_MASK"),
			ipv4,
		)
		if err != nil {
			return nil, fmt.Errorf("OpenConnect supplied an invalid %s route at index %d", name, index)
		}
		result = append(result, prefix)
	}
	return result, nil
}

func validateOpenConnectRouteSelectors(environment func(string) string, base string) error {
	for _, suffix := range []string{"_PROTOCOL", "_SPORT", "_DPORT"} {
		value := environment(base + suffix)
		if value != "" && value != "0" {
			return fmt.Errorf("OpenConnect %s route selectors are not supported", base)
		}
	}
	return nil
}

func parseOpenConnectRoutePrefix(addressText, prefixText, maskText string, ipv4 bool) (netip.Prefix, error) {
	if strings.Contains(addressText, "/") {
		prefix, err := netip.ParsePrefix(addressText)
		if err != nil || prefix.Addr().Is4() != ipv4 {
			return netip.Prefix{}, errors.New("invalid route prefix")
		}
		return prefix.Masked(), nil
	}
	address, err := netip.ParseAddr(addressText)
	if err != nil || address.Is4() != ipv4 {
		return netip.Prefix{}, errors.New("invalid route address")
	}
	prefixLength, err := strconv.Atoi(prefixText)
	if err != nil {
		prefixLength = -1
	}
	if err != nil && ipv4 {
		mask := net.ParseIP(maskText).To4()
		if mask != nil {
			var bits int
			prefixLength, bits = net.IPMask(mask).Size()
			if bits != 32 {
				prefixLength = -1
			}
		}
	}
	maximum := 128
	if ipv4 {
		maximum = 32
	}
	if prefixLength < 0 || prefixLength > maximum {
		return netip.Prefix{}, errors.New("invalid route prefix length")
	}
	return netip.PrefixFrom(address.Unmap(), prefixLength).Masked(), nil
}

func (policy openConnectRoutePolicy) usesDirectPath() bool {
	return policy.ipv4.includesConfigured || len(policy.ipv4.excludes) != 0 ||
		policy.ipv6.includesConfigured || len(policy.ipv6.excludes) != 0
}

func (policy openConnectRoutePolicy) useVPN(packet []byte) (bool, error) {
	destination, err := packetDestination(packet)
	if err != nil {
		return false, err
	}
	return policy.useVPNAddress(destination), nil
}

// Both the guest packet route and host SOCKS route use the gateway's policy.
// Route choice is made before dialing, never as a fallback after VPN failure.
func (policy openConnectRoutePolicy) useVPNAddress(destination netip.Addr) bool {
	if _, forced := policy.vpnDNS[destination.Unmap()]; forced {
		return true
	}
	family := policy.ipv6
	if destination.Is4() {
		family = policy.ipv4
	}
	useVPN := !family.includesConfigured
	bestPrefixLength := -1
	for _, prefix := range family.excludes {
		if prefix.Contains(destination) && prefix.Bits() > bestPrefixLength {
			useVPN = false
			bestPrefixLength = prefix.Bits()
		}
	}
	// vpnc-script installs excludes first and includes second. Matching that
	// behavior means an equally specific include wins, while the longest route
	// otherwise decides.
	for _, prefix := range family.includes {
		if prefix.Contains(destination) && prefix.Bits() >= bestPrefixLength {
			useVPN = true
			bestPrefixLength = prefix.Bits()
		}
	}
	return useVPN
}

func packetDestination(packet []byte) (netip.Addr, error) {
	if len(packet) < 1 {
		return netip.Addr{}, errors.New("empty guest IP packet")
	}
	switch packet[0] >> 4 {
	case 4:
		if len(packet) < 20 {
			return netip.Addr{}, errors.New("truncated guest IPv4 packet")
		}
		headerLength := int(packet[0]&0x0f) * 4
		totalLength := int(binary.BigEndian.Uint16(packet[2:4]))
		if headerLength < 20 || headerLength > len(packet) || totalLength != len(packet) {
			return netip.Addr{}, errors.New("malformed guest IPv4 packet")
		}
		return netip.AddrFrom4([4]byte(packet[16:20])), nil
	case 6:
		if len(packet) < 40 || int(binary.BigEndian.Uint16(packet[4:6])) != len(packet)-40 {
			return netip.Addr{}, errors.New("malformed guest IPv6 packet")
		}
		return netip.AddrFrom16([16]byte(packet[24:40])), nil
	default:
		return netip.Addr{}, errors.New("guest packet has an unsupported IP version")
	}
}
