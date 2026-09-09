# ADR 0057: Container-engine fallback for connection relays

Status: implemented, 2026-09-09. Builds on ADR 0056.

## Scope

On macOS 26+ ARM64 with the bundled Apple runtime, connection service isolates
continue to use the SDK. Otherwise a routed connection can use a local Docker
Desktop, OrbStack, or Podman engine. Direct host connections remain host processes.
This does **not** introduce Docker-backed interactive workspaces.

The headless connection protocol and clients are unchanged. The new
`IWorkspaceConnectionServiceProvider` adds service networking setup to the existing
isolation-provider contract. No new database-driver adapters are introduced.

## Network boundary

The relay has `--network none`: no Docker bridge, host networking, published ports,
host bind mounts, or Docker socket. A root-owned TAP process carries framed
Ethernet over private engine exec stdin/stdout. It never connects to an upstream.
The existing host Ethernet gateway supplies routing, synthetic DNS and delayed
TCP connection establishment. The host VPN/SSH route remains authoritative.

The TAP must acknowledge setup before the host reports readiness. Losing its
exec channel closes the nonpersistent interface. There is no alternative egress
interface, so a disconnected or crash-left container cannot fall back to NAT.
Normal disposal and startup failure remove the owned container. Reclaiming idle
containers after an abrupt desktop crash is tracked as `ghostshell-icgw`; it needs
cross-process ownership so it cannot remove another live app instance's relay.
The backend runs as UID/GID 1000 with no effective capabilities and
`no-new-privileges`. NET_ADMIN and CHOWN are available only to root setup/transport
commands. The container has a 1 GiB memory ceiling, a PID limit, bounded tmpfs
scratch, and no automatic restart policy.

Docker uses a read-only root filesystem and a configured resolver. Podman 6.1
rejects DNS flags with `network=none`, even `--dns=none`. Its root-owned overlay
remains writable so setup can replace the generated resolver before enabling
the packet path. Unprivileged backend processes cannot change that resolver.
Rootless Podman on the tested SELinux-enforcing machine also requires
`label=disable` for this specific TUN-using container. The host SELinux policy is
not modified. User namespaces, seccomp, capability restrictions and network
isolation remain in effect. Container relays share their engine's Linux kernel;
they are not equivalent to the dedicated-kernel workspace boundary.

## Engine and artifact ownership

Engine discovery checks Docker first, then Podman. Docker's context is resolved
to a local Unix endpoint. Podman accepts a local Unix endpoint or loopback SSH
connection to its local machine. Remote engines are rejected. Every command
captures that endpoint; subsequent context changes cannot redirect a live relay.
No engine is installed, started or reconfigured automatically by the application.
GUI launch discovery also checks `/opt/podman/bin`, the [official macOS
installer location](https://github.com/podman-container-tools/podman/blob/main/docs/tutorials/macos_autostart.md), in addition to Homebrew and Docker/OrbStack paths.

Relay images are built on demand from a digest-pinned Ubuntu base and a verified
backend archive. OS packages are obtained before the relay sees any connection
secrets. Images are reused by payload/recipe hash and executed by immutable image
ID. Provisioning uses the engine's ordinary download connectivity; connection
traffic does not. Rootfs provisioning never contains a password or vault key.

ARM64 and x64 backend archives contain the UI-free .NET worker and Linux packet
transport, with integrity manifests and license evidence. Only their descriptors
ship in the Mac app. `GHOSTSHELL_BACKEND_ARCH=x64 ./scripts/build-workspace-backend.sh`
builds the x64 archive. The native gateway build also supports osx-x64/linux-x64.
This does not by itself complete all other native dependencies for an Intel Mac
application release.

## Local verification

Opt-in tests create and remove their own relay containers, never use saved
credentials, and route to synthetic local test servers:

```sh
export GHOSTSHELL_TEST_RELAY_ARCHIVE="$PWD/native/artifacts/workspace-backend-build/distribution/GhostShell-workspace-backend-arm64.tar.gz"
export GHOSTSHELL_TEST_RELAY_GATEWAY="$PWD/native/artifacts/osx-arm64/ghostshell-workspace-gateway-darwin-arm64"
./.dotnet/dotnet test tests/GhostShell.Infrastructure.Tests -c Release --filter FullyQualifiedName~Container_relay
# Set GHOSTSHELL_TEST_RELAY_ENGINE=podman to exercise Podman's CLI and local machine.

export GHOSTSHELL_WORKSPACE_BACKEND_ARCHIVE="$GHOSTSHELL_TEST_RELAY_ARCHIVE"
./.dotnet/dotnet test tests/GhostShell.Architecture.Tests -c Release --filter FullyQualifiedName~Container_relay_SQL
```

The network test checks private DNS, IPv4/IPv6, remote loopback, absence of a
direct interface, worker capabilities, and traffic blocking before startup and
after gateway loss. The SQL test exercises the stock SqlClient driver, TDS login
redirects, transient retry, failover, exact TLS pins/original SNI, and revocation
during login. It is a synthetic protocol fixture, not a production SQL Server.
For x64 under a compatible engine emulator, set
`GHOSTSHELL_TEST_RELAY_ARCHITECTURE=x64` and both backend archive variables to the
x64 archive; also set `GHOSTSHELL_WORKSPACE_BACKEND_X64_ARCHIVE` to that archive.

Supported engine/OS combinations require execution testing. Building the x64
payload on ARM64 is not proof of native Intel hardware compatibility. Publication
remains subject to the existing dependency-review and release gates.

Local results on 2026-09-09: OrbStack ARM64 and x64 emulation passed the routing
and SQL fixtures; rootless Podman 6.1.1 passed both on ARM64. Linux relay unit
tests also ran under x64 emulation, including idle-I/O cancellation. The x64 run
exposed a TUN/Go epoll incompatibility, fixed by nonblocking Linux poll for the
transport and bounded cancellation of both frame pumps. Docker Desktop itself,
an older macOS host and physical Intel hardware were not available for this run.
