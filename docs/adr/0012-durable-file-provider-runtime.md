# ADR 0012: Durable file-provider runtime and profile manager

- Status: Accepted
- Date: 2026-07-22

## Context

The provider-neutral file API and protocol adapters existed, but the desktop registered only one
hard-coded local provider. Durable `FileProviderProfile` definitions therefore could not create a
live adapter, appear in File Viewer, be tested, or be selected by a saved File Viewer panel. A
catalog refresh also must not invalidate an operation or transfer already using the previous SDK
client set.

## Decision

`IFileProviderProfileRuntime` is the application-facing lifecycle and diagnostic port.
`CatalogFileProviderRuntime` subscribes to `IDefinitionCatalog`, materializes Local, S3 and
S3-compatible, SFTP, FTP/FTPS, SMB, and WebDAV definitions, and exposes the same runtime as both
`IFilePanelClient` and `IFileTransferQueueClient`. The non-durable `builtin.files.home` profile is
always retained as a safe local starting point.

Refresh builds a complete adapter generation before one atomic swap. Each ordinary operation holds
a generation lease. A queued transfer holds its lease until the queue reaches a terminal state.
Removed generations and their owned AWS or HTTP clients are disposed only after all such leases are
released. Terminal transfer snapshots remain visible after their provider generation retires.

S3 and WebDAV SDK clients are deferred until the first real operation. This keeps catalog loading
non-interactive and resolves an opaque `SecretRef` only at an adapter execution boundary. S3
credentials are a bounded OS-vault JSON value with `accessKeyId`, `secretAccessKey`, and optional
`sessionToken`; WebDAV, FTP, and SMB passwords are UTF-8 vault values. SFTP reuses the referenced
SSH connection and resolves its connection-scoped authentication only while opening a session.
No value is stored in SQLite, a profile DTO, a diagnostic, or a UI view model.
SFTP and terminal SSH also reuse the same durable per-connection host-key file through the
Application `ISshHostKeyTrustStore` boundary. The adapter supplies validated raw public-key
material, not an independently asserted fingerprint. Unknown, changed, and malformed-store
failures remain distinct typed results. The provider editor uses the existing bounded connection
review workflow for explicit first trust or changed-key replacement, then retests the provider.
An S3 profile without a credential reference is explicitly anonymous and never falls through to an
ambient AWS environment, shared config file, or instance-metadata credential chain.
Deferred initialization failures cross the application boundary as sanitized typed provider errors,
and remain retryable on a later operation. Replacing a provider-scoped vault value explicitly reloads
the adapter generation so an already materialized client cannot continue using the prior credential.

The Files settings surface manages persisted profiles with kind-specific validation, bounded root
tests, live typed diagnostics, and opaque secret selectors. A provider-scoped credential can be
created from Secrets after the profile has an identity, then selected while editing. Saved-screen
File Viewer panels persist `FileProviderProfileId`; a missing provider is explicit and must be
repaired before saving.

## Current limitations

- An S3 `RootPrefix` selects the initial browser prefix; authorization and confinement below that
  prefix must still be enforced by the bucket credential policy. Prefixes that cannot be represented
  as safe hierarchical segments are rejected during adapter materialization.
- S3/WebDAV SDK credential objects necessarily retain their credential for the client lifetime.
  Asura clears the resolved vault buffer immediately and disposes the client when its adapter
  generation retires, but cannot zero vendor-managed strings.
- Provider tests perform one bounded root listing; they do not recursively enumerate, mutate, or
  prove every optional server capability.

## Consequences

Durable profiles now drive the same live File Viewer and transfer queue used by the desktop, while
catalog edits cannot dispose clients underneath active work. Provider failures remain isolated: one
invalid profile produces a profile-scoped diagnostic and does not remove Home or other valid
providers. SFTP host-key repair does not create a second file-transfer trust decision: the reviewed
connection key immediately governs both terminal SSH and the next SFTP session.
