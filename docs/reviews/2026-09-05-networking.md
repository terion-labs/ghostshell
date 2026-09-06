# Networking deep review, 2026-09-05

## Implementation follow-up

The review below is the historical baseline. A subsequent user-authorized implementation pass
replaces WireGuard's external SOCKS engine with an embedded packet engine, adds bundled OpenVPN
Core, fixes proxy/Tailscale DNS attachment, preserves browser storage identity across runtime
recreation, and routes terminal/MCP/SSH/Git consumers through their workspace brokers. Database
TLS and Redis discovery routing were corrected where the libraries expose the required hooks.

Live checks using the user-supplied WireGuard, Proton OpenVPN, and authenticated HTTP proxy all
passed host HTTPS requests through the real production providers. Each also passed a real
Apple-container guest DNS/HTTPS test and fail-closed route removal. WireGuard and OpenVPN guest
tests additionally passed raw-IP ping; WireGuard passed IPv6 ping and HTTPS too. Credentials stayed in an in-memory vault or owner-private
temporary inputs and were not added to this repository. A subsequent concurrent sustained check
completed 13 rounds per connection over roughly 12 minutes: all 78 HTTPS responses were HTTP 200.
This is not a live Tailscale test or coverage of every VPN server configuration.

Tailscale follow-up (`ghostshell-3m8`): a fresh memory-only instance of the bundled 1.98.2
daemon reproduced rejection of a named `--exit-node` before the CLI even read its auth-key
file. The CLI resolves names against the pre-login peer map. GhostShell now runs `up`
with an explicitly cleared exit-node selection, then `set --exit-node=...` after login.
Neither stage publishes a workspace route; both must succeed before readiness checks and
session publication. Regression tests cover first login, persisted-identity reconnect,
and cleanup with distinct errors for login versus exit-node selection failures. This
reproduction did not read a real auth key or authenticate to the user's tailnet.
The user subsequently confirmed login succeeds but exit-node selection still fails in the
same tailnet. `ghostshell-r11` maps the pinned CLI's missing-peer, unadvertised-exit-node,
ambiguous-name, and self-selection errors to separate safe messages, without exposing raw
peer or server diagnostics. The live rejection cause is still awaiting the new test result;
these diagnostic changes must not be reported as a working Tailscale connection.

Development-bundle follow-up (`ghostshell-6hc`): live inspection found an empty
`/opt/ghostshell/bin` inside a running isolate while its host bind source contained the
gateway binary. The development launcher had deleted and replaced that source directory.
It now preserves the mounted directory inode when replacing the bundle and restores it
on an installation failure. Regression tests execute the actual replacement block for
fresh/existing bundles, incomplete/linked payloads, and failed installation. A missing
helper still fails closed, with a workspace-restart message instead of nested CLI errors.
The user authorized restarting the affected container; its helper became executable again
and its default route remained absent. No workspace reset or new VPN login was performed.

SDK boundary follow-up (`ghostshell-e5a`) supersedes the CLI guest-router and development
mount workaround above. The default macOS provider now owns a Containerization 0.42.0 VM
directly. Its single virtio NIC terminates at a host datagram socket; gVisor provides host-side
ARP, neighbour discovery, forwarding and translation into the existing provider packet channel.
There is no vmnet attachment, custom guest router, guest firewall command or guest proxy-env
requirement. SDK workspaces start fresh; migration is deliberately excluded and old CLI disks
are left untouched. Kernel, init filesystem, runtime, Swift compatibility library and legal/source
evidence are bundled and checksum-verified.

Real SDK tests passed fresh Ubuntu provisioning, routed DNS/HTTPS, no egress without a route,
fail-closed disconnect and persistent restart. Native VM tests also cover 24 concurrent execs,
PTY input/resizing, host-UID writable and read-only shares, and a 1,376,632-byte download.
The download/setup tests exposed Darwin `ENOBUFS` during normal NIC congestion; the adapter now
retries for a bounded 31 ms and then drops that frame instead of killing the workspace route.
Cancellation and permanent socket errors still terminate it. Race tests exposed unnecessary
local TCP worker pools in forwarding-only stacks; they now register gVisor's stateless parser
without endpoint workers, while retaining its packet/conntrack implementation. Twenty repeated
teardown race tests pass without increasing their deadlines.

SDK exec resolves bare guest commands through `/usr/bin/env --`, never the host PATH.
Released leases cannot create new launches, and generation-specific socket pairs keep previously
created launches from addressing a replacement VM. Failed or cancelled bootstrap never promotes
an incomplete disk and route death cancels package installation immediately. See the
[implementation contract](../architecture/host-owned-workspace-network.md) for packet limitations.

Final SDK verification: the supplied WireGuard profile passed guest DNS/HTTPS, fail-closed
disconnect and persistent restart through the new host NIC attachment. The fresh direct-route
Ubuntu test passed in 65 seconds; the WireGuard test passed in 58 seconds. The final
`./scripts/check.sh --full` passed, including Go race/vet checks, native OpenVPN contract tests,
Swift lifecycle tests, the zero-warning Release build and all managed test projects. Runtime
payload hashes, exact production-source receipt, virtualization-only entitlement and bundled
Swift dependency closure also verified. These SDK live checks cover direct and WireGuard;
the other live provider evidence above predates this NIC replacement, and Tailscale remains
unverified against a real tailnet in this pass.

| Current path | Implementation and evidence |
| --- | --- |
| WireGuard | Embedded host packet engine replaces wireproxy; host HTTPS, isolated IPv4/IPv6, DNS and fail-closed teardown passed live |
| OpenVPN | Bundled Core engine with inherited packet socket, no host TUN; supplied Proton TCP profile passed host and isolated guest traffic |
| AnyConnect | Existing host OpenConnect packet path retained; shared readiness, DNS retry and lifecycle regressions passed; user reported working, no new live login |
| HTTP/HTTPS/external SOCKS proxy | TCP plus DNS over the selected proxy; supplied authenticated HTTP proxy passed host and isolated guest traffic; arbitrary UDP is not advertised |
| Tailscale | Bundled userspace daemon, verified loopback UDP association and private routed DNS; deterministic tests passed, live tailnet deferred |

The simplification pass reused one host packet projection for VPN engines and one proxy-environment
projection for terminal/MCP launches. Obsolete wireproxy/ocproxy/libevent packaging was removed.
The macOS ARM64 bundle checksum manifest and dynamic-library closure were verified: only bundled
or Apple system libraries are needed. This does not certify Linux-host or Windows packaging.

Remaining constraints: non-isolated arbitrary child processes can ignore proxy environment
variables; HTTP/HTTPS and the external SOCKS adapter are TCP+DNS transports; Tailscale has no
arbitrary-IP adapter (it supports verified TCP/UDP and private routed DNS). MySQL VerifyFull now
validates the original hostname and trust through a relay, but its SNI remains the relay address
and OS revocation downloads retain the provider's OS networking behavior. Oracle TCPS/TNS stays
explicitly unsupported through the relay: a real pinned ODP.NET 23.7 test reproduces an empty
`CONNECT :1522` authority when the logical host cannot resolve on the host. The speculative
workaround was removed rather than weakening TLS identity (`ghostshell-ntw.25`). SQL Server
server-directed routing still needs a driver transport boundary (`ghostshell-ntw.24`). These are not
fixed by green tests elsewhere and prevent an unconditional “all connections” claim.

See [ADR 0054](../adr/0054-workspace-network-routing.md) for the current implementation mapping.
The final `./scripts/check.sh --full` passed after integration: locked restore, dependency audit,
format verification, Release build (zero warnings/errors), all managed test projects, native Go
tests/race/vet, and both OpenVPN Core contract tests. The 25 managed projects ran 7,337 tests:
7,327 passed, zero failed, and ten opt-in tests were skipped. Optional external integrations remain opt-in
and are skipped by the default gate. The latest explicitly enabled native integration run passed
all four direct/WireGuard/OpenVPN/authenticated-proxy guest tests (27 seconds). Native Go race/vet
and OpenVPN Core contract tests also passed. Live Tailscale remains deferred to user configuration.

## Historical review baseline (before the implementation above)

Reviewed `codex/workspace-networking` at `3943983`, compared with `main`. The checkout was clean at review start. Three parallel reviewers covered packet correctness/security, consumers/architecture, and packaging/tests. This is a review, not an implementation pass. No production code was changed, committed, or pushed. Test credentials are not included here.

## Verdict

The host-owned isolation boundary works in the exercised direct-gateway test, and the supplied WireGuard and HTTP proxy work through their real host provider implementations. The feature is **not protocol-complete or ready for an all-connections guarantee**. OpenVPN is absent, isolated proxy/Tailscale attachment fails, and several application-owned consumers bypass or misapply the selected route.

The largest security finding is the bundled WireGuard SOCKS dependency's unauthenticated, wildcard-bound UDP relay. Address that before treating WireGuard isolation as ready.

## What was tested

| Path | Result | Limits |
| --- | --- | --- |
| Supplied WireGuard configuration | Bundled `wireproxy` validation passed; actual `HostUserspaceVpnTransport` returned two DNS servers and loaded example.com and google.com with HTTP 200 | Host placement; not a live WireGuard guest test or long-running soak |
| Supplied authenticated HTTP proxy | Actual `ProxyNetworkConnectionProvider` started and loaded both HTTPS sites with HTTP 200 | Host placement; returned zero DNS servers, which blocks isolated attachment |
| Real Apple-container direct gateway | Explicitly enabled `Native_packet_gateway_routes_guest_dns_and_https_then_closes_fail_closed` passed | Tested guest default route, IPv4 ping, DNS, HTTPS, route removal and router shutdown; not every VPN or IPv6 destination |
| AnyConnect | User reports the recent fix now works; native regression/race tests passed | No new live AnyConnect login during this review |
| Proton OpenVPN | Blocked by missing implementation | Credentials were not submitted. A server `.ovpn` file is also needed for later interoperability testing |
| Tailscale | Static review only | Live configuration deferred at the user's request |

`./scripts/check.sh --full` passed. Native `go test -race -count=1 ./...` and `go vet ./...` passed separately. Optional integration tests remain skipped in the full gate; the Apple-container gateway test above was enabled explicitly. Passing these tests does not cover the gaps below.

Live provider tests used temporary, owner-private test files and an in-memory vault, not the user's saved catalog. Test-owned VPN processes were stopped and temporary credential copies removed. The original WireGuard file was unchanged. The native isolation test created and removed its own container/network. No host-wide VPN routes were installed.

## Findings

### P1: WireGuard's UDP relay is reachable outside loopback

[HostUserspaceVpnTransport.cs:149](../../src/GhostShell.Infrastructure/HostUserspaceVpnTransport.cs#L149) configures only the TCP SOCKS listener on loopback. The bundled wireproxy v1.1.3 dependency, `github.com/things-go/go-socks5@v0.0.5`, opens its UDP listener with `net.ListenUDP("udp", nil)` at `handle.go:178`. For a zero-address UDP ASSOCIATE request, it does not constrain the sender to the TCP peer. The gateway's tun2socks client sends exactly that request.

A local-only reproduction using the exact dependency and a fake upstream confirmed that a datagram sent through the host's non-loopback interface, from a different address than the TCP control peer, reached the relay and received a response. No external traffic or VPN credentials were used for that reproduction. LAN reachability is subject to the host firewall, but the implementation itself imposes no correct interface/session boundary.

Fix the bundled implementation or replace this path with a packet backend that enforces loopback binding and association ownership. Merely authenticating the TCP SOCKS handshake does not fix the UDP sender check. Add a regression against the packaged engine. Tracked in `ghostshell-ntw.17`.

### P1: OpenVPN is unimplemented

[HostUserspaceVpnTransport.cs:93](../../src/GhostShell.Infrastructure/HostUserspaceVpnTransport.cs#L93) returns `openvpn_host_userspace_adapter_missing` unconditionally. Both placements ultimately depend on this host transport, and the bundle contains no OpenVPN engine. The current test asserts rejection, not traffic. Implement and bundle the userspace adapter before advertising usable OpenVPN support. Tracked in `ghostshell-ntw.1`.

### P1: Isolated proxy and Tailscale always fail DNS preparation

[BundledWorkspacePacketGatewayBackend.cs:504](../../src/GhostShell.Infrastructure/BundledWorkspacePacketGatewayBackend.cs#L504) rejects sessions with no DNS servers. `ProxySession` never supplies them; the Tailscale success path at [HostUserspaceVpnTransport.cs:598](../../src/GhostShell.Infrastructure/HostUserspaceVpnTransport.cs#L598) omits them too. A WireGuard configuration without an explicit DNS field also encounters this restriction.

This explains why a provider test can succeed while isolated attachment fails. Keep DNS within the chosen route, but implement an explicit provider-appropriate DNS path. Tests need to compose concrete provider sessions with the real gateway contract instead of substituting fake sessions that already have DNS. Tracked in `ghostshell-ntw.18`.

### P1: SSH masters can cross workspace boundaries

[ConnectionCommandExecutor.cs:438](../../src/GhostShell.Infrastructure/ConnectionCommandExecutor.cs#L438) derives its SSH control socket from connection/authentication identity, not workspace route identity. Two workspaces using the same saved SSH profile can reuse the first workspace's master transport. Different proxy commands do not prevent the collision; a local `ssh -G` comparison confirmed the same expanded control path.

Include workspace/route ownership in multiplexing identity and verify route-local cancellation. Test two workspace brokers and the same SSH profile. Tracked in `ghostshell-ntw.19`.

### P1: First-time SSH host-key inspection bypasses routing

[RuntimeWorkspaceViewModels.cs:3008](../../src/GhostShell.App/ViewModels/RuntimeWorkspaceViewModels.cs#L3008) calls the global security runtime before routed launch preparation. [SshNetHostKeyScanner.cs:28](../../src/GhostShell.Infrastructure/SshNetHostKeyScanner.cs#L28) creates an ordinary direct connection. New VPN-only SSH hosts cannot complete trust setup, and inspection ignores the selected route/kill switch. File-provider repair uses the same runtime.

This is an existing direct-socket path left outside the new feature's routing coverage, not a newly introduced scanner. Route first-use and changed-key inspection through the workspace connector. Add a connector-only SSH fixture. Tracked in `ghostshell-1be.2`.

### P1: Governed Git remote reads undo proxy injection

[GitRepositoryClient.Governed.cs:1213](../../src/GhostShell.Git/GitRepositoryClient.Governed.cs#L1213) runs remote reads with an environment that clears proxy variables. Its options at [line 1316](../../src/GhostShell.Git/GitRepositoryClient.Governed.cs#L1316) explicitly clear `http.proxy` too. This preexisting sanitization runs after the new workspace launch injection, so application-owned remote reads on local repositories bypass the selected route.

Preserve protection against untrusted repository configuration while supplying the trusted workspace transport. Verify that a local HTTPS destination is contacted only through the recording broker. Ordinary local-repository SSH remotes also need a routing test; local terminal proxy variables alone do not configure OpenSSH. Tracked in `ghostshell-ntw.20`.

### P1: Database routing loses the logical TLS hostname

[DatabasePanelClient.cs:1443](../../src/GhostShell.Databases/DatabasePanelClient.cs#L1443) now applies the default relay to all network databases, including Direct non-isolated workspaces. At line 1469 it rewrites the server address to `127.0.0.1`. Driver endpoint rewrites do not preserve the original certificate identity. Redis similarly rewrites its endpoint without supplying `SslHost`.

Hostname-verifying TLS configurations that previously validated a certificate for the real server now validate against loopback unless the user supplied a separate identity override. Separate transport addressing from TLS identity. Test real hostname-valid and invalid certificates for the supported drivers; do not fix this by disabling validation. These driver-specific TLS scenarios were identified from code, not exercised against live databases in this review. Tracked in `ghostshell-ntw.21`.

### P1: Non-isolated browser persistence still depends on a random port

[DesktopBrowserRendererViewFactory.cs:93](../../src/GhostShell.Desktop/DesktopBrowserRendererViewFactory.cs#L93) supplies the broker endpoint as the browser route identity. `HostWorkspaceSocksProxy` does not implement the existing stable `BrowserProfileRouteIdentity` contract. [CefBrowserProfileStore.cs:502](../../src/GhostShell.Browser/CefBrowserProfileStore.cs#L502) therefore includes the random port in its persistent state key. A restarted host workspace restores a different cookie namespace.

Use the existing durable workspace identity contract and test a restart with a changed listener port. Reopened `ghostshell-ntw.13` for the host case; the isolated implementation already supplies a stable identity.

### P2: Deleting NO_PROXY from overrides leaves inherited values intact

[WorkspaceNetworkConnectionRuntime.cs:111](../../src/GhostShell.App/WorkspaceNetworkConnectionRuntime.cs#L111) removes `NO_PROXY` from a launch dictionary. [ConnectionCommandExecutor.cs:252](../../src/GhostShell.Infrastructure/ConnectionCommandExecutor.cs#L252) overlays that dictionary on inherited process environment, so an ambient `NO_PROXY=*` survives and bypasses proxies in cooperating programs. Carry explicit removal semantics or override with empty values. Verify the actual spawned process, not only the launch dictionary. Tracked in `ghostshell-ntw.22`.

### Other P2 findings

- [native host/run.go:161](../../native/workspace-network-gateway/internal/host/run.go#L161) advertises UDP merely because an upstream URI is SOCKS5. `ProxySocksAdapter` supports CONNECT only, even when wrapping HTTP/HTTPS. Fixing DNS alone does not make this a UDP-capable route. Carry concrete capabilities and negotiate UDP support. Tracked with `ghostshell-ntw.9.2`.
- WireGuard readiness at [HostUserspaceVpnTransport.cs:197](../../src/GhostShell.Infrastructure/HostUserspaceVpnTransport.cs#L197) checks a local listening socket, not a peer handshake. Later monitoring observes process exit only. An unreachable peer can stay "Connected". Use provider handshake/session evidence, without requiring public Internet access.
- [openconnect_socks.go:74](../../native/workspace-network-gateway/internal/host/openconnect_socks.go#L74) tries only the first DNS answer. Try alternate addresses within the same routing policy and bounded timeout. This must not introduce VPN-failure fallback to Direct.
- [RedisPanelSessionFactory.cs:30](../../src/GhostShell.Redis/RedisPanelSessionFactory.cs#L30) rejects multi-endpoint/Sentinel configurations when the newly mandatory default relay is present, including Direct mode. Add discovery/failover coverage or explicitly limit support. Tracked with `ghostshell-ntw.21`.
- The mandatory [check.sh](../../scripts/check.sh) does not run native networking tests. Include Go tests/vet in the required gate on a supported runner. Tracked in `ghostshell-ntw.23`.

## Architecture and bundling

The isolation design is appropriate: guest TUN and authenticated packet channel, host-owned provider, no VPN credentials or VPN executable required in the guest. The direct native test verifies that this boundary works. Isolated AnyConnect has the intended raw-packet adapter. WireGuard currently uses guest TUN to host tun2socks to wireproxy, so it carries TCP/UDP rather than arbitrary IP protocols. OpenVPN is missing. The accepted ADR describes some intended implementations as if they already exist and still mentions ocproxy in the active AnyConnect path. Update it to distinguish shipped behavior from planned adapters.

Non-isolated routing is necessarily best-effort for arbitrary child software, but that does not excuse the application-owned bypasses above. Standard proxy variables are injected for local launches and MCP, and SSH launches get a proxy command. Shared connector wiring exists for browser, file providers, database relays, AI HTTP and MCP HTTP. Each path still needs integration tests that demonstrate use of the connector rather than only its presence in constructors.

For the current macOS ARM64 distribution, all connection-engine and gateway checksums verified. `otool -L` found bundled `libopenconnect` and Apple system dependencies only, with no Homebrew dependencies. The Linux ARM64 guest helper is statically linked. OpenVPN is not bundled because it is not implemented. These checks do not establish a complete Linux-host or Windows distribution.

## Simplification recommendations

S1, recommended first: remove unused ocproxy packaging from `build-macos-connection-engines.sh`. No runtime C# caller resolves it now. Its libevent/lwIP build, payload entries and associated validation lists can go together under packaging tests.

S2: reuse `BrowserProfileRouteIdentity` for host workspaces. Delete random-port dependence instead of introducing another browser persistence mechanism.

S3: define one authenticated proxy-environment projection for workspace launches and MCP, including inherited-variable clearing. Remove repeated URI credential construction and six-variable lists.

S4: derive packet capabilities from the actual provider contract rather than the proxy URI scheme. Remove false SOCKS-implies-UDP assumptions and misleading readiness states.

S5: after capability correction, consider removing unused native HTTP/HTTPS adapter branches, since production providers already normalize these through `ProxySocksAdapter`. Confirm callers and tests before deletion.

No simplification was applied. Keep behavior fixes and structural changes separate, with characterization tests before refactoring. A broad rewrite would obscure these concrete defects.

## Validation needed before sign-off

1. Add failing regressions for the UDP relay and application-owned route bypasses, then verify the fixes against packaged binaries and real spawned processes.
2. Test concrete providers through both placements, including routed DNS, IPv4/IPv6, supported UDP, unsupported traffic, route switching and fail-closed teardown.
3. Add database TLS identity, browser restart persistence, shared-SSH-profile and ambient-NO_PROXY tests.
4. Implement/bundle OpenVPN, then test the Proton server configuration and credentials. Test Tailscale separately once configured.
5. Run the full managed gate, native tests/race/vet, packaging checks and a sustained multi-workspace traffic test. Do not infer protocol completeness from mocked process-start tests.
