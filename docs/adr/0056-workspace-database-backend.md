# ADR 0056: Execute isolated SQL operations in a workspace backend

Status: accepted, first implementation slice, 2026-09-08.

## Problem

The workspace VM already has a host-controlled default gateway, but SQL drivers
still ran in the desktop process. Each driver needed a socket callback, a
loopback relay with TLS-name repair, or a source patch to follow that route.
Moving only query execution missed metadata and schema-renderer connections.

## Decision

Extract the existing owned database worker into `GhostShell.DatabaseBackend`
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
listening TCP port, public RPC server, or credential file is introduced.
The backend accepts the original connection string, not a host loopback port.
Database credentials cross private IPC only when their operation is requested;
the OS vault and VPN credentials remain on the host.

One process owns one complete operation. Preserve the ready/execute handshake,
bounded typed value streaming, cancellation, and unknown-outcome handling after
dispatch. Never retry a possibly executed mutation automatically. Each operation
has a private guest scratch directory addressed by an opaque ID. The host owns
its cleanup through a separate control-channel command, including cancellation;
a guest lease prevents cleanup while its worker is still using the directory.
The guest checks its own 2 GiB working-set budget; measuring the host exec bridge
alone would not protect against a guest provider allocation. Schema rendering
stays in a cancellable host render worker but receives a detached graph, not
database connection authority.

The Linux ARM64 self-contained payload is a separate on-demand, checksum-pinned
release asset. The macOS application contains its descriptor only. Development
uses the same verified sidecar path. The archive includes its runtime/native
dependency closure, an entry manifest and notices; it excludes UI and CEF.
The guest installer verifies the archive before extracting into private staging.
No connection failure falls back to host execution.

## Current limits and next steps

- Explicit SSH database tunnels and non-isolated workspace connections retain
  their existing host worker and routing implementation. They must not be
  silently rerouted or dropped. This slice does not yet remove the SqlClient
  fork; the guest does not use its custom socket transport, but host callers do.
- Other file, Redis, browser and agent-provider backends have not moved. Browser
  rendering remains native on the host. Move additional capabilities only after
  equivalent lifecycle, authorization and streaming tests exist.
- Local file database paths in the guest name guest files. This is not an
  implicit host filesystem mount. Explicit mounted paths remain governed by
  the workspace's mount configuration.
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

## Verification

Keep characterization tests for the extracted host worker. Add executor tests
that fail if a provider is opened in the host, real worker metadata and typed
value round trips, detached graph rendering, pinned download failure tests, and
an opt-in real SDK VM test. That test must use a fresh private workspace, cover
binary transfer and guest file persistence, and leave no user profile or vault
changes. Run the full repository gate locally before landing.
