# ADR 0007: Structured connection planning and credential preflight

- Status: Accepted; credential-execution gap completed by ADR 0014
- Date: 2026-07-22

## Context

Local, SSH, Docker, and WSL connections eventually feed the same terminal-session boundary. Building a single command string would introduce quoting and option-injection defects, flatten important SSH and recovery states, and make it easy to copy credentials into definitions, logs, or process arguments. At the time of this decision, the terminal engine did not yet provide the lifetime-bound secret broker needed to deliver stored SSH passwords, private keys, passphrases, or secret environment variables safely; ADR 0014 subsequently completed that boundary.

## Decision

`Asura.Application` owns the reusable connection-transport ports.
`IConnectionRuntime` provides per-kind planning, typed progress, typed
failures, test reports, and non-secret interactive open plans.
`IConnectionCommandExecutor` executes one bounded structured command through
the same prepared transport. `Asura.Infrastructure` implements Local,
SSH, Docker, and WSL adapters and a router selected by `ConnectionKind`.
Terminal consumes the interactive plan, while monitoring, file authentication
preparation, and future governed modules consume bounded command execution;
they do not reconstruct per-kind launch arguments.

On Unix desktop hosts, repeated bounded SSH commands use an OpenSSH control
connection scoped to the application process and the exact saved connection
identity. The first command authenticates normally; later statistics, process,
and other command-backed module operations multiplex over that transport.
`ControlPersist` is deliberately short so closing the last consumer releases
the remote connection without a separate daemon lifecycle. A changed endpoint,
username, host-key policy, authentication mode, or credential reference gets a
different control path and cannot inherit an older authenticated connection.
Platforms without OpenSSH multiplexing retain the bounded one-process fallback.
Command-backed remote monitors keep their normal two-second sampling cadence
while healthy and back off to at most thirty seconds after consecutive capture
failures. This prevents an unavailable or rate-limiting endpoint from turning a
monitor panel into an authentication retry loop; a successful capture restores
the normal cadence.
Statistics and Process Monitor sessions created for the same connection identity
and execution configuration also share one process sampler, even when their
requests contain separately materialized immutable profile snapshots. Overlapping
reads coalesce onto one bounded `ps` capture, so opening both panels does not
double remote command load and both panels project a coherent observation.
Unexpected native or parser failures are classified at that sampler boundary and
never escape as an opaque session-host engine failure.

Every launch and probe uses an absolute executable plus an ordered argument list with `UseShellExecute = false`; adapters never concatenate a shell command. Process stderr is bounded, classified inside Infrastructure, and replaced by fixed application errors. Unknown and changed SSH host keys, authentication failure, missing runtimes, permission denial, timeout, offline endpoints, missing containers, and missing WSL distributions remain distinct states with explicit recovery actions. SSH plans retain their authentication mode and host-key policy.

SSH host identity is inspected through SSH.NET `2025.1.0` (MIT) before a verified desktop launch. Infrastructure retains the candidate public-key bytes behind an opaque, five-minute review ID and exposes only algorithm and SHA-256 fingerprint. Unknown, trusted, changed, and explicitly-unverified identities are distinct dispositions. `AcceptNew` may atomically add an unknown key; it never replaces a changed key. Replacing a changed key requires a separate explicit action against the exact review snapshot. Trust is stored as an owner-only, per-connection OpenSSH `known_hosts` file using a derived `HostKeyAlias`; the launch plan binds both the alias and exact file and disables fallback to global host files. When a connection has no Asura pin yet, an exact host, port, algorithm, and public-key match in the current user's standard OpenSSH `known_hosts` files may bootstrap that pin. Revoked, different, malformed, and inaccessible OpenSSH entries are never imported. A compare-and-swap prevents an in-process concurrent review or bootstrap from replacing a newer decision.

Connection diagnostics use the same planning and vault preflight, then authenticate stored password/private-key profiles with SSH.NET inside Infrastructure. Resolved bytes use the exact connection scope and `ConnectionAuthentication` purpose and are cleared after the bounded diagnostic connection. SSH.NET requires an immutable CLR string while parsing an encrypted private-key passphrase; .NET cannot deterministically clear that temporary string, so its lifetime remains limited to the probe. SSH-agent/system-configuration diagnostics continue through bounded OpenSSH because SSH.NET does not expose the platform agent/configuration behavior Asura needs.

SSH-agent and system-configuration plans request `AddKeysToAgent=yes`.
`ConnectionAuthentication.None` on an SSH profile means Asura has no
app-managed credential and OpenSSH owns authentication through its normal
configuration; the editor presents this as **System configuration**, not
**None**. When OpenSSH obtains a configured identity through platform behavior
such as the macOS Keychain, the identity becomes available for delegated
signing through the agent without putting private-key bytes or a passphrase in
Asura. SDK-backed SSH channels first inspect the agent; if it is empty,
they execute the bounded diagnostic through `IConnectionRuntime.TestAsync`,
then inspect the agent again. The bootstrap therefore retains the transport's
typed offline, timeout, authentication, and host-key failures while reusing the
same executable, endpoint, credential broker, and exact host-key binding as
Terminal. It is authentication preparation only; protocol operations remain
in their maintained SDK adapters.

Remote desktop plans now report `BoundedBackoff`. The desktop panel retries only retryable `Retry`/`Reconnect` failures, at most four times with cancellable one, two, four, and eight second delays. A healthy session resets the attempt counter. A normal terminal exit remains a manual reconnect so typing `exit` cannot reopen a session unexpectedly. Startup commands remain post-live and one-shot. Their batch keeps one request ID and idempotency key across renderer recreation and transient retries; the session host fingerprints the terminal write by session and SHA-256 command-text hash before replaying a prior success. Lease authorization is enforced on first execution, while a renderer's replacement lease can retrieve the stored success. A changed session cannot silently reuse a possibly accepted batch. Commands are cleared only after a confirmed write, while audit failure after delivery is surfaced without replaying side effects. Retryable dispatch failures are paced after the immediate first attempt with deterministic one, two, then five second delays capped at five seconds, so live-session polling cannot flood terminal input or audit storage.

Definitions continue to contain only `SecretRef` values. During planning and testing, adapters resolve every required credential with a `Connection` scope and `ConnectionAuthentication` purpose through `ISecretVault`, then immediately dispose the returned material. Plans retain only typed opaque secret requirements. Secret values never enter `TerminalLaunchRequest`, progress, process arguments, errors, or diagnostic output.

A plan containing secret requirements is not independently executable authorization. The SSH.NET diagnostics boundary may authenticate with stored credentials without exposing them. At the time of this decision, external OpenSSH password/private-key terminal launch remained fail-closed after vault preflight. ADR 0014 now supplies the required lifetime-bound connection credential broker; a plan is executable only after that broker replaces the original launch with its one-use helper launch. SSH agent and system-configuration tests may continue to use a bounded non-interactive endpoint probe.

## Consequences

- UI and future headless/ACP clients can render stable progress, diagnostics, fingerprints, trust decisions, reconnect countdowns, and repair actions without parsing process text.
- Hostile whitespace or option-looking profile values remain individual arguments and are placed after option boundaries where supported.
- Credential presence and authorization can be tested without an insecure askpass script or secret-valued launch argument/environment snapshot.
- ADR 0014 provides the connection-owned secret broker and ties helper-held authentication material to one terminal session.
- Startup-command dispatch fails closed when the audit trail is unavailable before delivery. Started and succeeded, failed, or cancelled outcomes use the closed audit-detail codec with only command count and typed error code; command text is structurally absent.
- SSH startup directories are applied after the OpenSSH destination boundary by a bounded `/bin/sh -c` script with the saved directory passed as a separately POSIX-quoted positional argument. The path remains data even when it contains whitespace, quotes, newlines, substitutions, or command separators. The plan explicitly warns that this option requires a POSIX-compatible target with `/bin/sh`; it does not claim Windows OpenSSH or arbitrary non-POSIX login-shell support. Environment variables continue through OpenSSH `SendEnv`, with a separate warning that the remote server must permit each name through `AcceptEnv`.
- SFTP now supplies validated raw public-key material through the shared Application trust-store boundary, so terminal SSH and File Viewer use this same durable per-connection OpenSSH binding and review workflow.
- System OpenSSH configuration and platform credential-store behavior can
  prepare agent identities for in-process SSH consumers without exporting
  private-key material.

## Alternatives rejected

- Shell-escaped command strings are platform-specific and remain vulnerable to quoting mistakes.
- Putting passwords in argv or environment variables exposes them to process inspection and diagnostics.
- Writing private keys to unmanaged temporary files creates cleanup, permission, crash-recovery, and lifetime hazards.
- Flattening all probe failures into `connection_failed` would prevent safe host-key review and targeted repair flows.
