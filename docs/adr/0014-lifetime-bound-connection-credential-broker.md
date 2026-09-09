# ADR 0014: Lifetime-bound connection credential broker

- Status: Accepted
- Date: 2026-07-22

## Context

ADR 0007 deliberately left stored SSH password, private-key, passphrase, and connection
environment execution fail-closed. A terminal process needs those values after planning, while the
durable connection and `TerminalLaunchRequest` boundaries must remain non-secret. Passing values in
argv, serializing them into the launch environment, generating an askpass script containing a
password, or leaving a private key in an unmanaged temporary file would make normal diagnostics and
process inspection credential disclosure paths.

Local shells, Docker, WSL, and SSH also need one consistent execution boundary for connection-scoped
secret environment variables. Docker can forward a variable by name, WSL uses `WSLENV`, and OpenSSH
uses `SendEnv`; none requires placing the value in argv.

## Decision

`Asura.Application` owns `IConnectionCredentialBroker` and its non-secret request. A prepared
`ConnectionOpenPlan` retains its opaque `ConnectionSecretRequirement` values for inspection and
audit, but records that an executable broker launch has been prepared. Existing desktop and Quick
Terminal paths can therefore execute the helper launch without receiving a secret value.

`Asura.Infrastructure` implements one current-user named-pipe ticket per planned launch. A
ticket has an independent random pipe name, ticket ID, and 256-bit bearer token; is bound to the
exact `ConnectionId`; accepts at most three invalid claims; expires after 30 seconds; and succeeds
only once. A successful or exhausted ticket is removed, so it cannot be replayed. Requests and
responses use a versioned, length-bounded binary protocol. The vault is resolved only after the
claim is authenticated. Authentication material uses `ConnectionAuthentication`; environment
material uses the distinct `ConnectionEnvironment` purpose. Returned material and all intermediate
byte buffers are disposed and zeroed best-effort.

The desktop executable has two early helper modes before database, UI, or recovery startup:

1. A connection helper claims the ticket and starts the original structured executable/argv.
2. When OpenSSH invokes `SSH_ASKPASS`, the same executable claims a second, one-use, prompt-role-
   constrained pipe and writes the password or passphrase directly as bytes to standard output.

Askpass prompt text is localized by OpenSSH, so it is treated as bounded, non-empty opaque text
rather than matched against English words. The one-use request carries and verifies its structured
password or private-key-passphrase role. The corresponding SSH launch permits only password or
only public-key authentication, disables keyboard-interactive and the alternate authentication
method, and uses non-interactive host-key policy; a different challenge therefore cannot consume
the credential.

When Asura is itself started through `dotnet Asura.dll`, helper re-entry preserves that
trusted managed-assembly prefix. Because `SSH_ASKPASS` accepts only an executable path, it uses the
sibling apphost and receives `DOTNET_ROOT` derived from the active `dotnet` host directory. Existing
architecture-specific `DOTNET_ROOT_*` values are left intact. Neither value contains user input or
credential material.

SSH password execution forces one `password` authentication prompt and disables keyboard-
interactive and public-key fallback. A stored private key is copied to an owner-only random file
held open with delete-on-close for exactly the helper/SSH lifetime. The helper inserts only that
non-secret path before OpenSSH's destination boundary, uses `IdentitiesOnly=yes` and
`IdentityAgent=none`, and best-effort overwrites and deletes the file after SSH exits. A passphrase
uses the same one-use askpass path. No credential value enters helper argv, the serialized launch
environment, `ToString`, errors, logs, or durable configuration.

Connection environment values are decoded only in the helper. They exist in the actual child
process environment because that is the requested destination, then their managed launch snapshot
is cleared immediately after process creation. Local shells receive the values directly. Docker
uses `--env NAME`, WSL augments `WSLENV`, and SSH uses `SendEnv=NAME`; all argv contain names only.
The immutable CLR strings required by `ProcessStartInfo.Environment` cannot be deterministically
zeroed, so their lifetime is minimized to the helper process.

### Desktop-v1 trust assumption

The PTY is created after planning and the current terminal-session API cannot inherit an already-
open anonymous handle. The one-use pipe name, ticket, and bearer token therefore travel as
structured helper argv. They are capabilities, not credential values, but a malicious process
already running as the same operating-system account can inspect them and race the intended helper.
Desktop v1 treats same-account processes as trusted. `PipeOptions.CurrentUserOnly`, exact
ticket/token/connection matching, short expiry, bounded invalid attempts, and removal on first
success prevent access by other OS users, cross-connection claims, and replay; they do not defend
against a hostile process with the user's own account privileges. A future session-factory contract
that can pass an inherited anonymous channel should remove this assumption without changing the
application broker port.

## Consequences

- Stored password/private-key SSH profiles and secret connection environments now start real
  terminal sessions through the same plan and reconnect flow as non-secret profiles.
- Vault access remains scope- and purpose-audited, while plans, recovery state, errors, and terminal
  launch snapshots remain free of credential values.
- Killing the helper closes the private-key handle; normal exit additionally overwrites and removes
  the file and its random owner-only directory.
- OpenSSH `SendEnv` works only when the server permits the named variable with `AcceptEnv`. A server
  that does not permit it ignores the value; SSH startup continues to carry the existing
  remote-startup warning rather than weakening transport safety.
- Password profiles intentionally do not emulate arbitrary keyboard-interactive challenges.
- Windows OpenSSH askpass and delete-on-close key behavior remain part of the packaged Windows
  terminal acceptance matrix, alongside macOS and Linux tests.

## Alternatives rejected

- Secret values in helper argv or the durable launch environment are observable and easy to log.
- A generated askpass script would put the credential on disk.
- A process-wide persistent SSH agent would retain a private key beyond its owning terminal.
- A raw private-key temporary file without an open delete-on-close owner has unsafe crash cleanup.
- SSH.NET shell emulation would bypass the selected system OpenSSH configuration and terminal
  behavior instead of closing the credential-delivery gap.
