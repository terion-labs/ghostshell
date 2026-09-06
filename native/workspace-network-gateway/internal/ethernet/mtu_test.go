package ethernet

import (
	"testing"
	"time"

	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/adapters/gonet"
	"gvisor.dev/gvisor/pkg/tcpip/header"
)

func TestUnfragmentedIPv6ReturnsPacketTooBig(t *testing.T) {
	h := newNetwork(t, true, false)
	address := tcpip.FullAddress{NIC: 1, Addr: tcpip.AddrFromSlice(h.remote.AsSlice()), Port: 9000}
	server, err := gonet.DialUDP(h.server, &address, nil, header.IPv6ProtocolNumber)
	if err != nil {
		t.Fatal(err)
	}
	defer server.Close()
	client, err := gonet.DialUDP(h.guest, nil, &address, header.IPv6ProtocolNumber)
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	_ = server.SetDeadline(time.Now().Add(10 * time.Second))
	if _, err := client.Write([]byte("query")); err != nil {
		t.Fatal(err)
	}
	buffer := make([]byte, 32)
	_, peer, err := server.ReadFrom(buffer)
	if err != nil {
		t.Fatal(err)
	}
	// Simulate a VPN packet whose upstream MTU is larger than the guest's.
	// This packet has never been fragmented and must not be fragmented here.
	h.serverLink.SetMTU(9000)
	if _, err := server.WriteTo(make([]byte, 1400), peer); err != nil {
		t.Fatal(err)
	}
	deadline := time.NewTimer(10 * time.Second)
	defer deadline.Stop()
	ticker := time.NewTicker(10 * time.Millisecond)
	defer ticker.Stop()
	for {
		h.mu.Lock()
		found := false
		for _, packet := range h.packets {
			if len(packet) >= 48 && packet[6] == 58 && header.ICMPv6(packet[40:]).Type() == header.ICMPv6PacketTooBig {
				found = true
				break
			}
		}
		h.mu.Unlock()
		if found {
			return
		}
		select {
		case <-deadline.C:
			t.Fatal("unfragmented IPv6 did not produce Packet Too Big")
		case <-ticker.C:
		}
	}
}
