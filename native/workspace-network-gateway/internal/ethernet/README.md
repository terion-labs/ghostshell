# Host-owned Ethernet gateway

`workspace-network-gateway ethernet --socket PATH` receives the VM's connected
Unix datagram NIC on descriptor 3. Stdin contains exactly 32 key bytes followed
by EOF. The socket parent must already exist with owner-only permissions. The
child creates an owner-only authenticated packet endpoint and prints `READY v1`
when listening. Readiness does not mean that a provider has connected yet.

The existing provider connects using protocol v1 and supplies its IP addresses,
DNS resolvers and MTU. The child speaks the protocol's guest role but runs only
on the host. Its gVisor stack has an Ethernet workspace NIC and a raw provider
NIC. There is no host network dialer, resolver, NAT interface or alternate path.

Supported forwarding is IPv4/IPv6 TCP, UDP and ICMP control, intersected with the
provider's own capabilities. Arbitrary IP protocols (`Other`) are not supported
by this NAT bridge and must not be advertised. The provider's unsupported address
families have no route. DNS sent to the stable gateway is translated to the first
same-family provider resolver, never to the host resolver. Both gateway DNS
addresses should be configured in the guest, including when one provider family
is unavailable.

ARP, IPv6 neighbour discovery, routing, NAT and connection tracking use gVisor.
The forwarding path in gVisor applies NAT before fragment reassembly, so separate
receive-only stacks perform bounded, expiring reassembly first. TCP/UDP fragments
are supported for both families, plus IPv4 ICMP fragments. Fragmented ICMPv6 and
complex IPv6 extension chains are blocked. Reassembled packets receive an
internal packet mark which allows gVisor to reconstitute source-originated IPv6
fragments after NAT, using fresh fragment IDs. Ordinary oversized unfragmented
IPv6 packets retain Packet Too Big handling. No custom fragment reassembler
or NAT checksum implementation is used.

These stacks register gVisor's TCP parser without its local TCP endpoint worker
pools. TCP sessions belong to the guest and provider, not to this forwarding
bridge. Local TCP/raw endpoint creation is explicitly unsupported.

Provider failure terminates the route and closes the child NIC descriptor.
All workers stop before the gVisor stacks and their NAT/fragment state are freed.
The Swift runtime owns its retained NIC descriptor and must stop the previous
child and drain queued NIC datagrams before starting a new route generation.
The child never removes a pre-existing socket or takes ownership of another lease.

Tests cover real packet-level ARP/NDP, TCP and large fragmented UDP round trips,
gateway DNS DNAT, source/reverse NAT, ICMP echo, IPv6 PTB, TCP ICMP quotations and
their translated checksums, malformed/unsupported
frames, source spoof rejection, uplink hairpin rejection, endpoint permissions,
key length, cancellation during accept/handshake/routing and provider teardown.
