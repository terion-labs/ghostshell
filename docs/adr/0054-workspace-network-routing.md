# ADR 0054: workspace network routing

## Status

Accepted.

## Context

Asura needs application networking defaults and complete per-workspace overrides.
A workspace can offer several proxy or VPN connections, but it sends traffic through one
selected connection at a time. The user must be able to select that connection, disable it,
and enable a kill switch from the window that owns the workspace.

The routing rule applies to every workspace backend. This includes terminals, browsers,
SSH and SFTP, file providers, databases, Docker, Git, monitors, MCP servers, and an AI agent
when that agent is configured to use the workspace environment.

## Decision

Network connections are reusable durable definitions. The first supported configurations are:

- SOCKS5, HTTP, and HTTPS proxies;
- WireGuard;
- OpenVPN;
- Cisco AnyConnect through OpenConnect;
- Tailscale through a selected exit node.

Configuration payloads and credentials that can contain secrets are stored in the secret
vault. Durable definitions contain only `SecretRef` values. Storing a password is optional:
when a password-authenticated proxy, AnyConnect, or OpenVPN profile has no password reference, every connection attempt prompts
for the password and keeps it only for that attempt. The same path is used by connection tests.
The transient password is passed to the selected host or isolated provider in memory, copied only
for startup when required, and cleared after the provider has consumed it.

An explicit rejection of a stored proxy, AnyConnect, or OpenVPN password triggers one
replacement prompt and one retry in the shared workspace runtime. The retry uses a
session-only password without changing the saved profile or its secret reference. After
the replacement establishes a usable route, the app asks whether to replace the existing
vault value. Declining preserves the old credential; cancellation or another rejection
does not loop. Timeouts, TLS/certificate errors, unavailable routes and locked/missing
vault entries do not trigger password replacement. Save failures keep the authenticated
route alive and show a notice. Metadata is checked again before saving to avoid overwriting
an entry edited while the prompt was open. This is not an atomic vault compare-and-swap.
Connection tests use this same flow; configuration documents and Tailscale auth keys are
not treated as passwords.

The application stores one `NetworkPolicy`, but its available connections are derived from the
global network-connection catalog rather than maintained as a second allow-list. A workspace
stores either no override, which inherits that application policy and the global catalog, or one
complete replacement with its own connection subset. A policy also contains a remembered
selection, an enabled flag, and a kill-switch flag. Only one connection can be active. Connection
chaining and ordered fallback are not part of this design.

Disabling networking uses the direct route and retains the remembered selection. If an enabled
connection fails and the kill switch is off, the workspace may use its direct route. If the
kill switch is on, the workspace exposes blocked egress until the selected connection works or
the user disables the policy.

Each isolated workspace runs in the bundled Apple Containerization SDK runtime. Its only virtio
NIC is attached to a host-owned Unix datagram socket, not vmnet, NAT or a bridged host interface.
Apple's normal VM agent assigns ordinary IPv4 and IPv6 interfaces, default gateways and DNS.
The host Ethernet adapter handles ARP, neighbour discovery and packet translation with gVisor;
it connects the virtual NIC to the selected host provider's authenticated packet channel. There
is no custom guest router, TUN helper, guest firewall command or proxy environment requirement.
Stopping the host route leaves the NIC with no external path, independently of guest privileges.
The exact runtime and packet contract is in
[the SDK network boundary](../architecture/host-owned-workspace-network.md).

The host gateway owns the selected connection and its credentials. A VPN executable is never
required in the workspace image. Its upstream is a provider adapter with raw packet input and
output. The direct adapter uses the host's ordinary network without changing host routes; VPN
adapters use app-owned userspace engines. One gateway is scoped to one isolated workspace, so
different workspaces can use different simultaneous network identities. The same session may also
expose authenticated loopback SOCKS5 and HTTP CONNECT endpoints for host-rendered workspace
consumers such as Chromium. Those endpoints are projections of the gateway route, not the route
used by guest processes.

A non-isolated workspace has no authoritative network boundary. It may use the loopback projection
for in-process clients and software that honors injected proxy settings, but a child process can
open an unrelated host socket. The UI must not describe that best-effort placement as isolated.

`IWorkspaceNetworkRuntime` owns one running workspace route. It resolves the selected provider,
starts and stops its session, applies kill-switch fallback, and publishes a
`WorkspaceNetworkSnapshot`. Panels consume only the resulting workspace egress through the
existing `WorkspaceRuntimeServices` boundary. They do not know which provider produced it.

Peer-bound agent HTTP fetches resolve A and AAAA records with DNS-over-TLS carried through the
workspace connector to the literal resolver endpoints `1.1.1.1:853` and `1.0.0.1:853`. They never
call the host resolver. The safety policy rejects the entire result if any returned address is
non-public, then connects the request to an admitted IP address while `SocketsHttpHandler` retains
the original URI hostname for TLS identity. If both routed DNS-over-TLS endpoints are unavailable,
the fetch reports DNS failure; it does not retry with host DNS or a direct socket.

VPN connection state describes the provider session, not access to an arbitrary public site.
Host userspace transports become connected after their provider reports a running session and the
local route endpoint is listening. OpenConnect hands its VPNFD to the bundled packet helper after
authentication and tunnel configuration. OpenVPN Core must report CONNECTED with a usable packet
configuration. Tailscale must report `BackendState` as `Running`. WireGuard completes a handshake
with its configured peers before opening the route and monitors handshake freshness thereafter.
The owning processes remain monitored and an unexpected exit marks
the session failed. Destination reachability is an operation result or an explicit diagnostic; a
split-tunnel VPN must not be called disconnected merely because it cannot reach the public Internet.

The window control reads that same snapshot. It can select any connection in the effective
policy and enable or disable networking. Starting, connected, failed, and kill-switch-blocked
states remain visible in the control.

## Provider mapping

- WireGuard uses an embedded userspace WireGuard engine with an in-memory TUN connected to the
  workspace gateway. A SOCKS-only `wireproxy` process does not satisfy isolated placement.
- OpenVPN uses OpenVPN 3 Core with a custom `TunBuilder` connected to the workspace gateway. The
  OpenVPN CLI does not satisfy isolated placement.
- AnyConnect uses OpenConnect script-tun raw packet I/O connected to the workspace gateway.
  Its non-isolated SOCKS projection uses the same bundled packet helper and userspace IP stack.
- Tailscale uses an app-owned userspace daemon and selected exit node. The host gateway bridges
  supported transports into the workspace NIC; this is not an arbitrary-IP Tailscale adapter. DNS
  uses its private resolver at `100.100.100.100` through that route. TCP and UDP are supported;
  UDP is advertised only after an association succeeds against the owned daemon, with a
  literal loopback relay and a connected UDP socket that filters unrelated senders. Closing
  the control connection closes UDP. ICMP and other IP protocols remain unsupported.
- A SOCKS5 proxy can carry full gateway UDP only when the server successfully negotiates UDP
  ASSOCIATE. HTTP and HTTPS proxies cannot carry arbitrary UDP or ICMP. Unsupported traffic fails
  closed and the UI reports the connection's concrete limitations.

Proxy profiles use DNS-over-HTTPS through their selected upstream to the literal bootstrap
address `1.1.1.1:443`, with TLS identity `cloudflare-dns.com`. This supplies guest DNS without
depending on TCP/53 support or leaking to the host resolver. HTTP/HTTPS proxies and the current
external SOCKS adapter carry TCP plus this DNS translation, not arbitrary UDP. VPN-supplied DNS
takes precedence; a WireGuard/OpenVPN profile with no DNS uses explicit public resolvers inside
the VPN, never a host-resolution fallback. These defaults are not connectivity health checks.

For the macOS ARM64 distribution, WireGuard and the userspace IP stack are statically linked into
the workspace helper. OpenVPN Core is a separate bundled raw-packet engine with static OpenSSL
and LZ4. OpenConnect and its bundled shared library, Tailscale, and all required legal notices
are packaged with checksums. `wireproxy`, `ocproxy`, and their unused dependencies are not shipped.
The macOS bundle also includes the signed SDK runtime, its Swift compatibility library, pinned
Linux kernel and init filesystem, and corresponding licenses and source archives. It does not
ship a custom Linux guest networking helper. This does not establish a supported Windows or
Linux-host distribution.

Fresh `ubuntu:24.04` workspace disks receive base tools during a separate bootstrap VM stage with
an explicit direct host route and no user mounts. They do not contain or install VPN clients.
Network definitions and credentials remain host-owned. Existing CLI workspaces are not migrated;
SDK workspaces start fresh without modifying the old CLI disks.

## Consequences

Different workspaces can hold different simultaneous network identities. Provider failures have
one typed path to the window and the kill switch. Adding support to a panel means consuming the
workspace route, not adding provider-specific settings. The SDK's public `VZInterface` attachment
point gives the bundled runtime ownership of the NIC without replacing Apple's VM lifecycle,
OCI handling, persistent disks or guest process agent. The installed `container` CLI and its
network plugin registry are not part of this runtime path.

Application and workspace settings must reject references to missing network profiles. Deleting
a profile must also reject or repair policies that still reference it. Runtime state is never
written into durable definitions.

Each loopback route broker generates a cryptographically random credential for one live workspace.
Route-aware clients authenticate with SOCKS5 username/password. The embedded Chromium renderer uses
the same listener through HTTP CONNECT or authenticated HTTP forwarding because Chromium does not
support SOCKS5 username/password; its proxy challenge is answered from the credential held in app
memory. Unauthenticated clients and credentials from another workspace are rejected. Credentials
are redacted from object formatting and are not placed in browser proxy preferences or route keys.

For a non-isolated workspace, owned terminal and stdio MCP processes may receive an authenticated proxy
HTTP CONNECT URI in the standard upper- and lower-case proxy variables. `NO_PROXY` and `no_proxy`
receive explicit empty overrides so inherited exclusions cannot survive. This avoids relying on
each child program's SOCKS support or local DNS behavior. The SOCKS endpoint remains available to
connector clients. SSH multiplexing is scoped to its live route identity, and
SSH terminal launches use the authenticated broker through an explicit `ProxyCommand`. This routes
software that honors those settings, but it is not a host security boundary: a child can ignore its
environment and open a direct socket, while another process running as the same OS user may inspect
that child's environment or Asura process memory. Universal enforcement of arbitrary child
traffic requires workspace isolation, where the host-only network and gateway are the authority.
The host kill switch is therefore authoritative for in-process connectors, not for arbitrary
non-isolated child binaries.

Peer-bound agent DNS currently depends on Cloudflare's public resolver endpoints. A workspace route
or upstream proxy that intentionally blocks TCP/853 makes peer-bound agent fetches fail closed. A
later settings contract may offer additional audited resolvers without introducing host-resolution
bootstrap or direct fallback.

ADR 0056 supersedes the per-driver connection relays. Routed SQL, Redis and remote
file operations now run inside the owned connection backend, using the guest
default gateway and original hostnames. This removes the SqlClient source fork,
SQL endpoint/TLS rewriting and SMB private-field reflection. Driver-created
redirects, retries, referrals and certificate checks inherit the guest network
boundary. Non-isolated Direct operations use an explicitly selected host child;
neither a failed route nor a closed workspace can select that child implicitly.
Service VMs carry TCP with route-resolved A/AAAA DNS; discovery requiring other
DNS record types or UDP is not silently sent through a host resolver. Explicit
certificate files use bounded private IPC import rather than host mounts.
