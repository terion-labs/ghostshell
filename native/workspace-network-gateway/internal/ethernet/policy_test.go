package ethernet

import (
	"context"
	"encoding/binary"
	"net/netip"
	"testing"
	"time"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/header"
)

func TestInvalidAndUnsupportedFramesDoNotReachProvider(t *testing.T) {
	prefix := netip.MustParsePrefix("10.22.0.2/32")
	r, err := newRouter(protocol.NetworkConfiguration{MTU: MTU, IPv4Address: &prefix, DNSServers: []netip.Addr{netip.MustParseAddr("192.0.2.53")}})
	if err != nil {
		t.Fatal(err)
	}
	defer r.close()
	frames := [][]byte{nil, {0}, make([]byte, 13), make([]byte, MTU+15)}
	// A valid IPv4 header with a spoofed source, unsupported VLAN, and an
	// IPv6 packet on an IPv4-only provider all fail closed.
	ip4 := make([]byte, 20)
	ip4[0] = 0x45
	binary.BigEndian.PutUint16(ip4[2:4], uint16(len(ip4)))
	ip4[8] = 64
	copy(ip4[12:16], netip.MustParseAddr("10.99.0.2").AsSlice())
	copy(ip4[16:20], netip.MustParseAddr("192.0.2.53").AsSlice())
	h := header.IPv4(ip4)
	h.SetChecksum(^h.CalculateChecksum())
	frames = append(frames, frame(header.IPv4ProtocolNumber, ip4), frame(0x8100, ip4))
	other := append(append([]byte(nil), ip4...), make([]byte, 4)...)
	copy(other[12:16], guest4.AsSlice())
	other[9] = 47 // GRE is deliberately outside the bridge's advertised protocols.
	binary.BigEndian.PutUint16(other[2:4], uint16(len(other)))
	header.IPv4(other).SetChecksum(0)
	header.IPv4(other).SetChecksum(^header.IPv4(other).CalculateChecksum())
	frames = append(frames, frame(header.IPv4ProtocolNumber, other))
	ip6 := make([]byte, 40)
	ip6[0] = 0x60
	ip6[6] = 59
	ip6[7] = 64
	copy(ip6[8:24], guest6.AsSlice())
	copy(ip6[24:40], netip.MustParseAddr("2001:db8::53").AsSlice())
	frames = append(frames, frame(header.IPv6ProtocolNumber, ip6))
	for _, data := range frames {
		r.injectFrame(data)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
	defer cancel()
	if packet := readLink(ctx, r.provider); packet != nil {
		t.Fatalf("blocked frame reached provider: %x", packet)
	}
}

func TestUntrackedProviderPacketsCannotHairpin(t *testing.T) {
	prefix := netip.MustParsePrefix("10.22.0.2/32")
	r, err := newRouter(protocol.NetworkConfiguration{MTU: MTU, IPv4Address: &prefix, DNSServers: []netip.Addr{netip.MustParseAddr("192.0.2.53")}})
	if err != nil {
		t.Fatal(err)
	}
	defer r.close()
	ip := make([]byte, 28)
	ip[0] = 0x45
	binary.BigEndian.PutUint16(ip[2:4], uint16(len(ip)))
	ip[8] = 64
	ip[9] = 17
	copy(ip[12:16], netip.MustParseAddr("192.0.2.53").AsSlice())
	copy(ip[16:20], netip.MustParseAddr("203.0.113.7").AsSlice())
	binary.BigEndian.PutUint16(ip[20:22], 53)
	binary.BigEndian.PutUint16(ip[22:24], 12345)
	binary.BigEndian.PutUint16(ip[24:26], 8)
	header.IPv4(ip).SetChecksum(^header.IPv4(ip).CalculateChecksum())
	r.injectPacket(ip)
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
	defer cancel()
	if packet := readLink(ctx, r.provider); packet != nil {
		t.Fatalf("provider traffic looped back to provider: %x", packet)
	}
}

func FuzzEthernetFrame(f *testing.F) {
	f.Add([]byte{})
	f.Add(frame(header.ARPProtocolNumber, make([]byte, 28)))
	f.Add(frame(header.IPv6ProtocolNumber, make([]byte, 40)))
	f.Fuzz(func(t *testing.T, data []byte) {
		prefix := netip.MustParsePrefix("10.22.0.2/32")
		r, err := newRouter(protocol.NetworkConfiguration{MTU: MTU, IPv4Address: &prefix, DNSServers: []netip.Addr{netip.MustParseAddr("192.0.2.53")}})
		if err != nil {
			t.Fatal(err)
		}
		defer r.close()
		r.injectFrame(data)
	})
}

func frame(number tcpip.NetworkProtocolNumber, payload []byte) []byte {
	data := make([]byte, 14+len(payload))
	header.Ethernet(data).Encode(&header.EthernetFields{SrcAddr: "\x02\x47\x53\x4e\x57\x02", DstAddr: GatewayMAC, Type: number})
	copy(data[14:], payload)
	return data
}
