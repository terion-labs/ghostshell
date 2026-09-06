package ethernet

import (
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/header/parse"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/waiter"
)

// forwardedTCP registers gVisor's TCP parser, not a local TCP endpoint stack.
// All TCP sessions terminate in the guest or provider. Starting gVisor's full
// TCP protocol would create a GOMAXPROCS-sized dispatcher pool in each of our
// three routing/reassembly stacks, despite none ever creating a TCP endpoint.
type forwardedTCP struct{}

func newForwardedTCP(*stack.Stack) stack.TransportProtocol { return forwardedTCP{} }

func (forwardedTCP) Number() tcpip.TransportProtocolNumber { return header.TCPProtocolNumber }
func (forwardedTCP) MinimumPacketSize() int                { return header.TCPMinimumSize }
func (forwardedTCP) Parse(pkt *stack.PacketBuffer) bool    { return parse.TCP(pkt) }
func (forwardedTCP) ParsePorts(packet []byte) (uint16, uint16, tcpip.Error) {
	if len(packet) < 4 {
		return 0, 0, &tcpip.ErrMalformedHeader{}
	}
	h := header.TCP(packet)
	return h.SourcePort(), h.DestinationPort(), nil
}

func (forwardedTCP) NewEndpoint(tcpip.NetworkProtocolNumber, *waiter.Queue) (tcpip.Endpoint, tcpip.Error) {
	return nil, &tcpip.ErrNotSupported{}
}
func (forwardedTCP) NewRawEndpoint(tcpip.NetworkProtocolNumber, *waiter.Queue) (tcpip.Endpoint, tcpip.Error) {
	return nil, &tcpip.ErrNotSupported{}
}

func (forwardedTCP) HandleUnknownDestinationPacket(stack.TransportEndpointID, *stack.PacketBuffer) stack.UnknownDestinationPacketDisposition {
	// There are no TCP services on the gateway itself. Reassembled packets are
	// consumed by the ingress handler before reaching this fallback.
	return stack.UnknownDestinationPacketHandled
}
func (forwardedTCP) SetOption(tcpip.SettableTransportProtocolOption) tcpip.Error {
	return &tcpip.ErrUnknownProtocolOption{}
}
func (forwardedTCP) Option(tcpip.GettableTransportProtocolOption) tcpip.Error {
	return &tcpip.ErrUnknownProtocolOption{}
}

// This parser owns no workers or endpoints.
func (forwardedTCP) Close()   {}
func (forwardedTCP) Wait()    {}
func (forwardedTCP) Pause()   {}
func (forwardedTCP) Resume()  {}
func (forwardedTCP) Restore() {}
