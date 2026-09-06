package ethernet

import (
	"encoding/binary"
	"net/netip"

	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
)

// installNAT delegates checksums, fragments, ICMP errors and reverse translation
// to gVisor conntrack. Only workspace-originated traffic is source-translated.
func installNAT(s *stack.Stack, guest, gateway, assigned netip.Addr, resolvers []netip.Addr) {
	v6 := guest.Is6()
	number := networkProtocol(guest)
	empty := stack.EmptyFilter4()
	if v6 {
		empty = stack.EmptyFilter6()
	}
	accept := stack.Rule{Filter: empty, Target: &stack.AcceptTarget{NetworkProtocol: number}}
	rules := []stack.Rule{accept}
	for _, resolver := range resolvers {
		if resolver.Is6() != v6 {
			continue
		}
		filter := empty
		filter.InputInterface = "workspace"
		filter.Dst = tcpip.AddrFromSlice(gateway.AsSlice())
		filter.DstMask = fullMask(guest)
		rules = []stack.Rule{{Filter: filter, Matchers: []stack.Matcher{dnsPort{}}, Target: &stack.DNATTarget{Addr: tcpip.AddrFromSlice(resolver.AsSlice()), NetworkProtocol: number, ChangeAddress: true}}, accept}
		break
	}
	postrouting := len(rules)
	filter := empty
	filter.Src = tcpip.AddrFromSlice(guest.AsSlice())
	filter.SrcMask = fullMask(guest)
	filter.OutputInterface = "provider"
	rules = append(rules, stack.Rule{Filter: filter, Target: &stack.SNATTarget{Addr: tcpip.AddrFromSlice(assigned.AsSlice()), NetworkProtocol: number, ChangeAddress: true}}, accept)
	acceptIndex := len(rules) - 1
	table := stack.Table{Rules: rules,
		BuiltinChains: [stack.NumHooks]int{stack.Prerouting: 0, stack.Input: acceptIndex, stack.Forward: stack.HookUnset, stack.Output: acceptIndex, stack.Postrouting: postrouting},
		Underflows:    [stack.NumHooks]int{stack.Prerouting: acceptIndex, stack.Input: acceptIndex, stack.Forward: stack.HookUnset, stack.Output: acceptIndex, stack.Postrouting: acceptIndex},
	}
	s.IPTables().ReplaceTable(stack.NATID, table, v6)
	// Reject source spoofing even for packets injected internally by a future
	// caller; the frame prefilter is not the policy's sole enforcement point.
	forward := stack.EmptyFilterTable()
	filter = empty
	filter.InputInterface = "workspace"
	filter.Src = tcpip.AddrFromSlice(guest.AsSlice())
	filter.SrcMask = fullMask(guest)
	filter.SrcInvert = true
	noHairpin := empty
	noHairpin.InputInterface = "provider"
	noHairpin.OutputInterface = "provider"
	forward.Rules = []stack.Rule{
		{Filter: filter, Target: &stack.DropTarget{NetworkProtocol: number}},
		{Filter: noHairpin, Target: &stack.DropTarget{NetworkProtocol: number}},
		accept,
	}
	forward.BuiltinChains[stack.Forward] = 0
	forward.Underflows[stack.Forward] = 2
	forward.BuiltinChains[stack.Input] = 2
	forward.Underflows[stack.Input] = 2
	forward.BuiltinChains[stack.Output] = 2
	forward.Underflows[stack.Output] = 2
	s.IPTables().ReplaceTable(stack.FilterID, forward, v6)
	if v6 {
		mangle := s.IPTables().GetTable(stack.MangleID, true)
		index := len(mangle.Rules)
		mangle.Rules = append(mangle.Rules, stack.Rule{Filter: empty, Target: &restoreSourceFragments{}})
		mangle.BuiltinChains[stack.Postrouting] = index
		mangle.Underflows[stack.Postrouting] = index
		s.IPTables().ReplaceTable(stack.MangleID, mangle, true)
	}
}

func fullMask(address netip.Addr) tcpip.Address {
	if address.Is4() {
		return tcpip.AddrFrom4([4]byte{255, 255, 255, 255})
	}
	return tcpip.AddrFrom16([16]byte{255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255})
}

type dnsPort struct{}

func (dnsPort) Name() string { return "workspace-dns" }
func (dnsPort) Match(_ stack.Hook, pkt *stack.PacketBuffer, _, _ string) (bool, bool) {
	if pkt.TransportProtocolNumber != header.TCPProtocolNumber && pkt.TransportProtocolNumber != header.UDPProtocolNumber {
		return false, false
	}
	transport := pkt.TransportHeader().Slice()
	if len(transport) < 4 {
		return false, true
	}
	return binary.BigEndian.Uint16(transport[2:4]) == 53, false
}
