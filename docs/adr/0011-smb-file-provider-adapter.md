# ADR 0011: Cross-platform SMB 2/3 file-provider adapter

- Status: Accepted
- Date: 2026-07-22

## Context

ADR 0008 requires an SMB adapter behind the same bounded `IFileProvider` contract as local, S3, WebDAV, SFTP, and FTP. SMB has server/share identity, domain authentication, negotiated dialect and transport properties, Windows-style reparse points and attributes, server-defined case behavior, sharing conflicts, and permanent mutations. The adapter must not implement the SMB wire protocol, store a resolved password in a durable profile, expose vendor types, claim ACL or resume behavior it does not implement, or flatten a structured `FileLocation` into a caller-provided UNC string.

## Decision

### Library and platform policy

The production transport uses `SMBLibrary` `1.5.7.1`, published 2026-07-13, through its .NET Standard 2.0 asset. The project is active, cross-platform, and implements SMB 1.0, 2.0, 2.1, and 3.0 clients and servers. GhostSHELL instantiates only `SMB2Client`, enables its SMB 3.1.1 negotiation path, and uses direct TCP hosting on port 445. It never offers SMB 1.0/CIFS and does not use NetBIOS port 139. The package remains a separate, unmodified managed assembly behind `SmbLibrarySessionFactory`; no SMBLibrary type appears in a public provider constructor or result.

SMBLibrary does not expose a socket or stream factory. For a workspace-routed session, the adapter therefore opens `server:445` through `IWorkspaceNetworkConnector`, relays that stream through a per-session unprivileged loopback port, and connects SMBLibrary only to that loopback endpoint. There is no direct fallback after a workspace connector has been selected. Authentication remains end-to-end between SMBLibrary and the remote server; the relay receives only encrypted or negotiated SMB wire bytes and never receives credentials as parameters.

Authentication and UNC paths still require the logical server name rather than the loopback address. The private compatibility boundary for the exact `SMBLibrary` `1.5.7.1` package verifies that its `m_serverName` field can preserve a probe value before credentials are resolved, then sets and verifies the requested logical name before connecting. If that self-test fails after a dependency change or trimming step, routed SMB is rejected explicitly. SMBLibrary follows DFS referrals by constructing a new direct client, so routed sessions reject its internal DFS file-store result immediately after tree connect and before exposing a session or executing a file operation.

SMBLibrary is licensed `LGPL-3.0-or-later`, not MIT. GhostSHELL remains MIT, while SMBLibrary keeps its own license. The macOS Native AOT distribution retains the applicable notices and publishes an exact source-and-rebuild path for the user's LGPL replacement rights. Organizations that cannot accept those obligations must obtain the vendor's commercial license or replace this private adapter. The project owner accepted the documented path for the exact macOS closure without an independent legal review; dependency drift requires a new decision.

### Identity, authentication, and transport state

An `SmbFileProviderOptions` stores a provider profile ID, opaque authority, bounded server and share names, remote root, response timeout, reconnect policy, and an `SmbAuthentication` choice. Password authentication stores only domain, username, and an opaque `SecretRef`. `SmbLibrarySessionFactory` resolves that reference with `FileProvider` scope and `FileProviderAuthentication` purpose only while opening a session. The copied UTF-8 byte buffer is zeroed immediately after decoding. Options and authentication objects have explicit safe formatting that omits the reference; diagnostics and mapped exceptions contain neither the password nor the reference.

SMBLibrary's login API requires an immutable CLR password string and retains authentication state for the network session; .NET cannot deterministically zero that string. GhostSHELL limits exposure by creating a fresh session per provider operation and disconnecting it on completion or cancellation. Guest authentication is explicit and produces a visible warning. Integrated Windows authentication, Kerberos, credential prompting, and account discovery are not implemented in this adapter.

The library internally negotiates SMB 2.0.2 through 3.1.1, signing, maximum read/write sizes, and SMB 3 encryption when the server or share requires it. Its public client API does not expose the selected dialect, signing state, or encryption state. GhostSHELL therefore always emits `smb_transport_security_unverified` and cannot currently require or attest encryption. This is a deliberate visible limitation, not an assumption that direct TCP is encrypted.

### Paths, links, metadata, and mutations

Callers continue to use hierarchical `FileLocation` segments. The adapter accepts no UNC path. It converts the already-confined remote path to SMB separators inside the vendor boundary and enforces a portable Windows-compatible name subset: traversal, backslashes, controls, wildcard/reserved punctuation, alternate-data-stream colons, overlong components, and trailing spaces or dots are rejected. Metadata and data handles use `FILE_OPEN_REPARSE_POINT`; a reparse-point leaf is reported as a link, and the shared remote-provider boundary refuses to navigate, read, write, rename through, or transfer through a reported link.

SMB entry revisions combine kind, size, last-write time, change time, and server file ID. They support best-effort conflict detection during one server's metadata lifetime but are not durable object versions, so `Versioning` is not advertised. Writes and copies use a unique sibling temporary file, bounded chunked I/O, a second precondition check, and rename-based commit with a best-effort backup/restore of an existing file. That sequence reduces loss but is not transactional across server failure, so `AtomicReplace` is not advertised. Move is copy/commit followed by source deletion and can return `PartialTransfer`. Deletes are permanent. A non-empty directory is rejected for a shallow delete; recursive traversal occurs only when the caller explicitly requests it, refuses reparse points, observes cancellation between entries, and can leave a partially deleted tree if a later entry fails.

File-name comparison is `ProviderDefined`: Windows shares are normally insensitive, but SMB servers, backing filesystems, and per-directory settings can differ. The adapter maps reparse, directory, file, size, and timestamp metadata but does not yet expose Windows attributes, owners, permissions, or ACLs through the common entry. It consequently does not advertise `Symlinks`, `Permissions`, or `AccessControlLists`.

### Streaming, cancellation, retry, and capability mapping

Reads and writes stream sequentially in chunks no larger than the server-negotiated SMB maximum and the provider's 1 MiB buffer limit. A single read chunk is returned by SMBLibrary as a byte array and a single write chunk is copied into its request buffer; whole files are never buffered by GhostSHELL. Ranged reads open the file at the requested offset. There is no persisted checkpoint, identity revalidation across sessions, durable upload resume, or server-side copy, so `ResumableTransfer` and `ServerSideCopy` remain disabled.

SMBLibrary exposes synchronous command methods. The adapter runs each command off the UI thread, checks cancellation before and after every command and transfer chunk, and aborts the client socket when cancellation is requested. An in-flight library command has no per-command cancellation token; cancellation can therefore wait for socket teardown or the configured response timeout, and an operating-system TCP connect may outlive that response timeout. The default command response timeout is 15 seconds and profiles may choose one second through two minutes. Only list/stat may reconnect once with a fresh session after a classified transient failure. Streamed reads, writes, rename, transfer, and delete are never replayed.

The adapter advertises `List`, `Stat`, `RangedRead`, `StreamingWrite`, `CreateDirectory`, `Rename`, `Copy`, `Move`, `Delete`, and provider-local `Pagination`. SMBLibrary's directory query materializes a complete directory result before the common cursor emits bounded pages, so this is not server-side pagination and exceptionally large directories remain a known memory-pressure limitation. Search, watch, discovery, ACL editing, checksums, durable versions, atomic replacement, server-side copy, and resume are not claimed.

Vendor status values are converted to sanitized typed errors for not-found, collision, access denied, wrong entry kind, non-empty directory, unsupported operation, invalid name, busy/share conflict, timeout, and broken-session cases. Vendor error text is never forwarded. Sharing conflicts are retryable I/O failures rather than false version conflicts.

## Verification

The SMB provider runs the shared deterministic file-provider conformance suite over the vendor-free remote-session seam. Adapter tests additionally cover structured path conversion, traversal and separator rejection, secret-scope construction, safe formatting, security diagnostics, capability honesty, sanitized NT status mapping, and the rule that metadata may retry once while mutations never replay. No test requires a live share, network discovery, domain account, or credential.

## Primary sources

- SMBLibrary package `1.5.7.1`, framework assets, publication date, repository, and `LGPL-3.0-or-later` license: <https://www.nuget.org/packages/SMBLibrary/1.5.7.1>
- SMBLibrary source, supported protocols/transports, cross-platform package, commercial-license note, and current activity: <https://github.com/TalAloni/SMBLibrary>
- SMBLibrary client examples for SMB2 tree connect, listing, chunked reads/writes, delete, and cross-platform Kerberos extension: <https://github.com/TalAloni/SMBLibrary/blob/master/ClientExamples.md>
- Microsoft SMB 2/3 protocol specification: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-smb2/5606ad47-5ee0-437a-bb4d-3c7fbe91892a>
- GNU LGPL version 3 text: <https://www.gnu.org/licenses/lgpl-3.0.html>

## Consequences

- macOS, Linux, and Windows can access SMB 2/3 shares without mounting them or installing a remote GhostSHELL agent.
- Profiles retain structured server/share identity and opaque credential references; resolved passwords exist only inside the execution adapter.
- Destructive operations use the same precondition and staging rules as other hierarchical remote providers.
- Transport-encryption attestation, exact dialect reporting, ACL/attribute editing, integrated authentication, discovery, true server-side pagination, and resumable transfers remain explicit future work.
- Shipping SMBLibrary carries LGPL distribution obligations that do not apply to the MIT-licensed SFTP and FTP dependencies.

## Alternatives rejected

- Implementing SMB packets directly would create an unacceptable security and interoperability burden.
- Shelling out to `mount_smbfs`, `mount.cifs`, or `net use` would mutate host-global mount state, complicate credential lifetime and cleanup, and make headless cross-platform behavior inconsistent.
- Treating an arbitrary UNC string as a `FileLocation` would erase provider-profile authorization and reintroduce separator and traversal ambiguity.
- Advertising ACLs, encryption, atomic replacement, or resume merely because the protocol can support them would misrepresent what this adapter can currently verify and execute.
