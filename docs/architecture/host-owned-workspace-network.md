# SDK-owned workspace runtime and network boundary

Implementation contract, 2026-09-05. This replaces the custom guest packet router
with fresh SDK workspaces. Migration is explicitly out of scope.

## Ownership

- A bundled `workspace-runtime` Swift process owns one workspace VM, its persistent
  root filesystem, and its control socket. It uses Containerization 0.42.0 directly.
- The VM has exactly one virtio NIC, attached to a connected Unix datagram socket
  through `VZFileHandleNetworkDeviceAttachment`. No vmnet/NAT/bridged attachment is
  permitted. Only Apple's normal VM agent configures the guest interface.
- The runtime retains the host end of that socket. A route lease starts the bundled
  `workspace-network-gateway ethernet` child with that socket as descriptor 3.
  Releasing the lease stops the child. Without it, packets have nowhere to go.
- VPN engines, credentials, DNS forwarding, address translation, and route policy
  remain outside the guest. Existing provider packet adapters are reused.

## Packet contract

- Descriptor 3 carries one complete Ethernet frame per datagram. The guest MTU is
  1280; the Virtualization.framework attachment uses its minimum hardware MTU of
  1500. Ethernet headers are additional. Guest IPv4 is `100.64.0.2/30`, gateway
  `100.64.0.1`; guest IPv6 is `fd00:4753:4e57::2/126`, gateway
  `fd00:4753:4e57::1`. These are independent point-to-point segments per workspace.
- The Ethernet child takes `--socket PATH` and an exactly 32-byte authentication
  key on stdin followed by EOF. It exposes the existing authenticated v1 packet
  channel as the guest-side endpoint, but executes entirely on the host.
- It prints `READY v1` after listening. The selected host provider then connects
  and supplies its existing network configuration. gVisor handles ARP, neighbour
  discovery and forwarding/address translation between the stable guest subnet
  and the provider's assigned IP addresses. DNS at the gateway is translated to
  the provider's resolver, never an implicit host resolver.
- Provider disconnect, invalid configuration, malformed frames, or gateway death
  cannot enable a direct route. Unsupported address families remain blocked.
- The Ethernet adapter supports TCP, UDP, ICMP echo and required ICMP control
  traffic. gVisor reassembles fragmented UDP before translation. Arbitrary IP
  protocols, fragmented ICMPv6 and unsupported IPv6 extension-header chains are
  blocked; the SDK attachment does not advertise the `Other` protocol capability.
- Route replacement stops the previous route before starting the next and drains
  pending NIC packets. No old flow or DNS state crosses a route generation.

## Runtime control

The native runtime exposes a private, owner-only Unix control socket. Requests are
bounded structured JSON, not shell strings. Operations cover prepare/serve,
structured exec (including stdin, stdout, stderr, exit and terminal resizing),
network route leases, and stop. Exact wire records live with the Swift runtime.
Route authentication keys are streamed over stdin, never passed as process
arguments. Structured exec requests preserve the existing application process
contract; they are not a transport for VPN credentials.

The .NET provider retains the existing `IWorkspaceIsolationProvider` contract.
SDK network metadata is distinct from legacy guest-helper metadata. The backend
must not issue guest router or guest firewall commands for an SDK binding.

## Persistence and rollout

Workspace state lives outside the replaceable app bundle. Stopping a VM never
deletes its root filesystem. SDK workspaces start fresh; existing Apple CLI disks
are neither migrated nor modified. The user explicitly excluded migration because
the application is not in production use.

Fresh default Ubuntu disks are unpacked atomically, then provisioned in a
temporary SDK VM with an explicit direct host route and no user mounts. Only a
successful installation and graceful stop promote the staged disk. Interrupted
setup keeps its original image identity and can be retried. Persistent workspace
VMs start with no route and receive only the route selected by the application.

Tests must cover SDK NIC attachment, persistent restart, structured exec and PTY,
ARP and IPv6 neighbour discovery, routed DNS/TCP/UDP, teardown and blocked-route
behaviour. The SDK runtime must be built and signed as part of the app bundle.
