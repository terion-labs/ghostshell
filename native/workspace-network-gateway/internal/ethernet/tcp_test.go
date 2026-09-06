package ethernet

import (
	"encoding/binary"
	"testing"
	"time"

	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/checksum"
	"gvisor.dev/gvisor/pkg/tcpip/header"
)

func TestForwardedTCPHasNoLocalEndpoints(t *testing.T) {
	p := newForwardedTCP(nil)
	if endpoint, err := p.NewEndpoint(header.IPv4ProtocolNumber, nil); endpoint != nil || err == nil {
		t.Fatal("forwarding parser exposed a local TCP endpoint")
	}
	if endpoint, err := p.NewRawEndpoint(header.IPv6ProtocolNumber, nil); endpoint != nil || err == nil {
		t.Fatal("forwarding parser exposed a local raw endpoint")
	}
	if _, _, err := p.ParsePorts([]byte{1}); err == nil {
		t.Fatal("accepted truncated TCP ports")
	}
}

func TestICMPQuotesRestoreTCPGuestTupleAndChecksums(t *testing.T) {
	for _, v6 := range []bool{false, true} {
		name := "ipv4"
		if v6 {
			name = "ipv6"
		}
		t.Run(name, func(t *testing.T) {
			h := newNetwork(t, v6, false)
			h.echo(t, "tcp", h.remote, 8443)
			h.mu.Lock()
			quoted := append([]byte(nil), h.packets[0]...)
			h.mu.Unlock()
			source := tcpip.AddrFromSlice(h.remote.AsSlice())
			destination := tcpip.AddrFromSlice(h.assigned.AsSlice())
			body := append(make([]byte, 8), quoted...)
			offset, protocolNumber, expectedType := 20, byte(1), byte(3)
			if v6 {
				offset, protocolNumber, expectedType = 40, 58, 2
				body[0] = expectedType
				binary.BigEndian.PutUint32(body[4:8], 1280)
				binary.BigEndian.PutUint16(body[2:4], ^checksum.Checksum(body,
					header.PseudoHeaderChecksum(header.ICMPv6ProtocolNumber, source, destination, uint16(len(body)))))
			} else {
				body[0], body[1] = expectedType, 4
				binary.BigEndian.PutUint16(body[6:8], 1280)
				binary.BigEndian.PutUint16(body[2:4], ^checksum.Checksum(body, 0))
			}
			packet := make([]byte, offset)
			if v6 {
				header.IPv6(packet).Encode(&header.IPv6Fields{PayloadLength: uint16(len(body)), TransportProtocol: tcpip.TransportProtocolNumber(protocolNumber), HopLimit: 64, SrcAddr: source, DstAddr: destination})
			} else {
				header.IPv4(packet).Encode(&header.IPv4Fields{TotalLength: uint16(offset + len(body)), TTL: 64, Protocol: protocolNumber, SrcAddr: source, DstAddr: destination})
				header.IPv4(packet).SetChecksum(^header.IPv4(packet).CalculateChecksum())
			}
			h.router.injectPacket(append(packet, body...))
			deadline := time.NewTimer(10 * time.Second)
			defer deadline.Stop()
			ticker := time.NewTicker(10 * time.Millisecond)
			defer ticker.Stop()
			for {
				var result []byte
				h.mu.Lock()
				for _, frame := range h.frames {
					if len(frame) < 14+offset+8+len(quoted) {
						continue
					}
					ip := frame[14:]
					isICMP := ip[0]>>4 == 4 && ip[9] == 1
					if v6 {
						isICMP = ip[0]>>4 == 6 && ip[6] == 58
					}
					if isICMP && ip[offset] == expectedType {
						result = append([]byte(nil), ip...)
						break
					}
				}
				h.mu.Unlock()
				if result != nil {
					inner := result[offset+8:]
					innerSource, innerDestination := header.IPv4(inner).SourceAddress(), header.IPv4(inner).DestinationAddress()
					guest := tcpip.AddrFromSlice(guest4.AsSlice())
					initial := uint16(0)
					if v6 {
						innerSource, innerDestination = header.IPv6(inner).SourceAddress(), header.IPv6(inner).DestinationAddress()
						guest = tcpip.AddrFromSlice(guest6.AsSlice())
						outer := header.IPv6(result)
						initial = header.PseudoHeaderChecksum(header.ICMPv6ProtocolNumber, outer.SourceAddress(), outer.DestinationAddress(), uint16(len(result)-offset))
					} else if header.IPv4(inner).CalculateChecksum() != 0xffff {
						t.Fatal("quoted IPv4 header checksum invalid after reverse NAT")
					}
					if innerSource != guest || innerDestination != source {
						t.Fatalf("quoted TCP tuple was not restored: %s -> %s", innerSource, innerDestination)
					}
					if checksum.Checksum(result[offset:], initial) != 0xffff {
						t.Fatal("ICMP checksum invalid after reverse NAT")
					}
					transport := inner[offset:]
					pseudo := header.PseudoHeaderChecksum(header.TCPProtocolNumber, innerSource, innerDestination, uint16(len(transport)))
					if checksum.Checksum(transport, pseudo) != 0xffff {
						t.Fatal("quoted TCP checksum invalid after reverse NAT")
					}
					return
				}
				select {
				case <-deadline.C:
					t.Fatal("TCP ICMP quotation did not return to the guest")
				case <-ticker.C:
				}
			}
		})
	}
}
