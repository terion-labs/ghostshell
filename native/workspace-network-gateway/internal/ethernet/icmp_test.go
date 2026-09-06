package ethernet

import (
	"bytes"
	"encoding/binary"
	"testing"
	"time"

	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/checksum"
	"gvisor.dev/gvisor/pkg/tcpip/header"
)

func TestICMPEchoIsTranslatedBothDirections(t *testing.T) {
	for _, v6 := range []bool{false, true} {
		for _, sameAddress := range []bool{false, true} {
			name := "ipv4"
			if v6 {
				name = "ipv6"
			}
			if sameAddress {
				name += "-proxy"
			}
			t.Run(name, func(t *testing.T) {
				h := newNetwork(t, v6, sameAddress)
				source := guest4
				if v6 {
					source = guest6
				}
				src, dst := tcpip.AddrFromSlice(source.AsSlice()), tcpip.AddrFromSlice(h.remote.AsSlice())
				body := append(make([]byte, 8), []byte("ping through provider")...)
				binary.BigEndian.PutUint16(body[4:6], 1234)
				binary.BigEndian.PutUint16(body[6:8], 1)
				var packet []byte
				if v6 {
					body[0] = 128
					icmp := header.ICMPv6(body)
					icmp.SetChecksum(header.ICMPv6Checksum(header.ICMPv6ChecksumParams{Header: icmp, Src: src, Dst: dst}))
					packet = make([]byte, 40)
					header.IPv6(packet).Encode(&header.IPv6Fields{PayloadLength: uint16(len(body)), TransportProtocol: 58, HopLimit: 64, SrcAddr: src, DstAddr: dst})
				} else {
					body[0] = 8
					binary.BigEndian.PutUint16(body[2:4], ^checksum.Checksum(body, 0))
					packet = make([]byte, 20)
					header.IPv4(packet).Encode(&header.IPv4Fields{TotalLength: uint16(20 + len(body)), TTL: 64, Protocol: 1, SrcAddr: src, DstAddr: dst})
					header.IPv4(packet).SetChecksum(^header.IPv4(packet).CalculateChecksum())
				}
				packet = append(packet, body...)
				h.router.injectFrame(frame(networkProtocol(source), packet))
				deadline := time.NewTimer(10 * time.Second)
				defer deadline.Stop()
				ticker := time.NewTicker(10 * time.Millisecond)
				defer ticker.Stop()
				for {
					matched := false
					h.mu.Lock()
					for _, data := range h.frames {
						if len(data) < 14+len(packet) {
							continue
						}
						ip := data[14:]
						offset := 20
						replyType := byte(0)
						if v6 {
							offset = 40
							replyType = 129
							if ip[0]>>4 != 6 || ip[6] != 58 {
								continue
							}
						} else {
							if ip[0]>>4 != 4 || ip[9] != 1 {
								continue
							}
						}
						if ip[offset] == replyType && binary.BigEndian.Uint16(ip[offset+4:offset+6]) == 1234 && bytes.Equal(ip[offset+8:], body[8:]) {
							matched = true
							break
						}
					}
					h.mu.Unlock()
					if matched {
						return
					}
					select {
					case <-deadline.C:
						t.Fatal("ICMP echo reply did not traverse NAT")
					case <-ticker.C:
					}
				}
			})
		}
	}
}
