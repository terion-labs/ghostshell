# ADR 0056: Execute connection work in a workspace backend

Status: accepted, implementation expanded 2026-09-09.

## Problem

The workspace VM already has a host-controlled default gateway, but SQL drivers
still ran in the desktop process. Each driver needed a socket callback, a
loopback relay with TLS-name repair, or a source patch to follow that route.
Moving only query execution missed metadata and schema-renderer connections.

## Decision

Extract the existing owned database worker into `GhostShell.ConnectionBackend`
and give it a UI-free `GhostShell.Backend database` executable. Keep the current
application contracts and provider implementations. All eleven SQL operation
families, including connectivity probes, catalogs, object metadata and counts,
now cross `IDatabaseOperationExecutor` when an executor is configured.

For an isolated workspace without an explicit SSH database hop:

```text
Desktop database panel
  -> IDatabaseOperationExecutor (typed, bounded stdin/stdout)
  -> owner-private SDK exec / vminitd channel (no PTY)
  -> GhostShell.Backend database, inside the workspace
  -> database driver -> guest default gateway -> host-selected route
```

This control channel does not use the guest's IP route. No new SSH server,
listening TCP port, public RPC server, or host credential store is introduced.
The backend accepts the original connection string, not a host loopback port.
Database credentials cross private IPC only when their operation is requested;
the OS vault and VPN credentials remain on the host.

For private service VMs, explicit host certificate and key file options are
imported over the same private IPC into per-operation guest scratch (0600).
Import is bounded to 16 known files, 4 MiB per file and 16 MiB total; symlinks,
implicit include paths and ambient host certificate stores are not imported.
Oracle wallet/TNS directories allow only recognized self-contained files.
Ordinary workspace operations keep their guest file-path semantics. Imports do
not rewrite the server name or disable the driver's TLS verification, and are
removed with the owning operation's scratch.

Non-isolated server connections with custom networking and explicit SSH database
hops use a private, mount-free service VM. This VM does not replace the user's host terminal or
local filesystem. Its sole network attachment is a host-owned gateway bound to
one captured route generation. For an SSH hop the host establishes and verifies
SSH, then supplies its authenticated SOCKS endpoint to that gateway. A failed
route retires the service and its operations; it cannot fall back to direct
networking or silently adopt a replacement route.

Non-isolated Direct connections without an SSH hop use the same owned backend
on the host. This is an explicit execution policy, not recovery from a failed
service VM. It preserves host authentication and file semantics, avoids a VM
where no network isolation is requested, and keeps Direct source builds usable
on other platforms. Changing the captured Direct route revokes active workers;
the next operation must select its execution location again.

The service gateway answers A/AAAA queries with per-VM synthetic addresses and
passes the original names in SOCKS CONNECT. DNS therefore occurs at the selected
route, including private SSH-side names. Mappings are bounded and never reused.
Service-only loopback translation lets `127.0.0.0/8` and `::1` refer to the
remote route without rewriting driver hostname/TLS identity. These rules are
not installed in ordinary workspace VMs, where loopback retains its usual
workspace-local meaning. SOCKS credentials travel over private stdin, not
command arguments, environment variables, or a saved configuration file.

The gateway completes a guest TCP handshake only after its upstream CONNECT
succeeds. Refused destinations therefore remain TCP connection refusals, so
stock drivers can perform their own normal failover without a driver patch.

For SQL, one process owns one complete operation. Preserve the ready/execute handshake,
bounded typed value streaming, cancellation, and unknown-outcome handling after
dispatch. Never retry a possibly executed mutation automatically. Each operation
has a private guest scratch directory addressed by an opaque ID. The host owns
its cleanup through a separate control-channel command, including cancellation;
a guest lease prevents cleanup while its worker is still using the directory.
The guest checks its own 2 GiB working-set budget; measuring the host exec bridge
alone would not protect against a guest provider allocation. Schema rendering
stays in a cancellable host render worker but receives a detached graph, not
database connection authority.

Redis uses one owned child per session, preserving selected database, topology,
binary values and subscriptions. Remote file providers use one child per provider
generation so that pagination cursors and provider state survive operations.
Existing file transfer governance, previews and catalog lifetimes remain on the
host. File callbacks expose only the selected profile's secrets, trust checks,
and bounded SSH authentication signatures; they do not expose a general vault
or signing service. An isolated file provider never advertises a host-local
path for preview shortcuts.

Routed AI provider HTTP requests use `HttpMessageHandler` backed by the same
owned backend. Provider parsing and model policy stay on the host. The child
performs HTTP/TLS and streams bounded response frames, without ambient proxy
settings, cookies, credentials or automatic redirects. Cancellation and route
revocation close the exchange; a possibly sent request is never replayed by the
bridge. Host-native browser rendering retains its browser-specific proxy boundary.

The Linux ARM64 self-contained payload is a separate on-demand, checksum-pinned
release asset. The macOS application contains its descriptor only. Development
uses the same verified sidecar path. The archive includes its runtime/native
dependency closure, an entry manifest and notices; it excludes UI and CEF.
The guest installer verifies the archive before extracting into private staging.
No connection failure falls back to host execution.

## Current limits and next steps

- The service route provides TCP and A/AAAA DNS, not arbitrary UDP or SRV/TXT
  discovery. It rejects unsupported DNS requests instead of using a host or
  public resolver. Provider protocols requiring those capabilities need an
  appropriate full workspace route; a TCP-only SSH tunnel cannot supply them.
- Local file database paths in the guest name guest files. This is not an
  implicit host filesystem mount. Explicit mounted paths remain governed by
  the workspace's mount configuration.
- Non-isolated SQLite remains host-local with extension loading disabled.
  Direct host DuckDB retains host file identity. Network-capable DuckDB with a
  custom route must run in an isolated workspace; the app rejects that host
  combination explicitly instead of permitting a route bypass.
- An explicit SSH hop cannot make a SQLite or DuckDB file path remote. Those
  combinations are rejected before opening a tunnel or VM. Guest FTP requires
  passive mode because the service route does not supply inbound port mappings;
  Direct host FTP retains active-mode support.
- The guest is part of the workspace execution trust boundary. This is not a
  secret-protection boundary against arbitrary code running as the same guest
  user. A remotely administered backend will need separate authenticated
  authority, capability negotiation and attachment/resumption semantics.
- A persistent headless/server session host is a later layer. Do not serialize
  UI handles or renderer objects from `ISessionHostClient`, or infer human
  authority from a caller-supplied actor label.
- Replacing generated SQL with the existing Calcite engine is independent work;
  this change preserves existing SQL, parameter and mutation semantics.
- Linux redistribution evidence must be approved before shipping the new
  sidecar. Local backend builds and tests do not imply a release approval.

## Resource ownership

Ordinary workspace memory is a finite ceiling derived from half the host's RAM,
including SDK overhead, rather than the old fixed 1 GiB setting. Backing memory
is touched on demand; this is neither unlimited memory nor a promise of dynamic
ballooning/reclamation. Concurrent workspaces still compete for host memory.
Compact service VMs use a separate 1 GiB guest ceiling and a 4 GiB sparse disk.
Their immutable provisioned disk template is verified and cloned; route-specific
disks and state are removed when their owning service is retired.

Cleanup drains workers and private scratch through the control channel before
stopping the VM, then disposes SSH and route authority. Failed stops retain the
exact lease for retry. The owner refuses to accumulate replacement VMs while an
earlier startup's cleanup remains unresolved.

## Verification

The local native fixtures create disposable SDK VMs without host mounts, real
user credentials or Keychain access. Their cleanup removes the VM and private
test state. The opt-in suites cover:

| Suite | Verified behavior |
| --- | --- |
| `WorkspaceServiceNetworkingNativeTests` | IPv4/IPv6, loopback aliases, private DNS through authenticated SOCKS, kill switch, 4 GiB disk and warm template clone |
| `WorkspaceDatabaseBackendNativeTests` | All eleven SQL operation families, SQLite binary values, DuckDB, guest-only file CRUD/transfer, real SFTP host-key verification and host-owned signing, Redis state/subscriptions, streamed HTTP and cancellation/spill cleanup |
| Service SQL Server fixture | Stock driver login redirects, transient retry, failover partner, original TLS server name, correct/wrong imported certificate pin, cancellation during login and revoked authority |
| Service Oracle fixture | Stock ODP.NET private DNS and listener redirects, original TLS server name, trusted matching certificate and wrong-host rejection across retries |

The SQL Server fixture speaks TDS locally; the Oracle listener fixture stops
before database authentication. Neither requires a public database or proves
every vendor's hosted deployment configuration. Unit and
architecture tests additionally cover bounded protocols, scoped credential
callbacks, missing/closed workspace registrations, route selection and the
absence of host fallback. Package tests bind the exact dependency and notice
closure and reject changed or linked inputs.

To repeat native tests on a supported Apple Silicon Mac after building the
gateway, workspace runtime and backend sidecar:

```sh
export GHOSTSHELL_TEST_SDK_RUNTIME_ROOT="$PWD/native/artifacts/osx-arm64/workspace-runtime"
export GHOSTSHELL_WORKSPACE_BACKEND_ARCHIVE="$PWD/native/artifacts/workspace-backend-build/distribution/GhostShell-workspace-backend-arm64.tar.gz"
./scripts/build-workspace-backend.sh --verify
./.dotnet/dotnet test tests/GhostShell.Infrastructure.Tests/GhostShell.Infrastructure.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WorkspaceServiceNetworkingNativeTests
./.dotnet/dotnet test tests/GhostShell.Architecture.Tests/GhostShell.Architecture.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WorkspaceDatabaseBackendNativeTests
```

These fixtures may download distro test tools into their disposable VM. They
never add those tools to the release payload. The normal `./scripts/check.sh
--full` gate runs separately; without the opt-in variables it skips the native
VM fixtures rather than requiring virtualization on every test host.
