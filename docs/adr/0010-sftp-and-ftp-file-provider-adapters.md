# ADR 0010: SFTP and FTP/FTPS file-provider adapters

- Status: Accepted
- Date: 2026-07-22

## Context

ADR 0008 requires real SFTP and FTP adapters behind the `IFileProvider` contract. Both protocols expose hierarchical paths, but they differ in transport security, authentication, metadata quality, feature discovery, reconnect behavior, and conflict guarantees. The adapters must not turn an SSH or FTP library into a second public API, expose secret values, silently weaken TLS or host-key policy, impose an arbitrary transfer-size ceiling, or claim safe resume/atomicity that the current contract cannot deliver.

## Decision

### SFTP

The production SFTP transport uses `SSH.NET` pinned to `2025.1.0`, commit `6390ede`, under the MIT license. The package targets .NET 8 and .NET 9; the .NET 10 application consumes its .NET 9 asset. It provides cancellation-aware SFTP listing, metadata, stream open, directory, rename, and delete APIs, including asynchronous upload/download improvements in the 2025.1 release.

An `SftpFileProviderOptions` references the existing durable SSH
`ConnectionProfile`. The file-provider authority is the connection ID, and the
adapter consumes the same endpoint, authentication references, keepalive
setting, and `SshHostKeyPolicy`. Password, private-key, and passphrase bytes
resolve from `ISecretVault` with connection scope only inside the SSH.NET
factory; profile, diagnostic, exception, and result types contain no secret
value.

System-agent and system-configuration profiles use the maintained
`SshNet.Agent` extension, which obtains compatible public identities from the
platform OpenSSH agent and delegates signing without exporting private-key
material. A raw agent socket does not apply OpenSSH identity-file configuration
or platform credential-store behavior by itself. When the agent is empty, the
adapter therefore asks the shared `IConnectionRuntime` transport to run its
bounded typed diagnostic. Both native authentication modes request
`AddKeysToAgent=yes`, so successful native OpenSSH authentication exposes the
configured signing identity through the agent; SFTP then re-reads the agent and
opens its SSH.NET channel. This preparation reuses Terminal's executable,
host-key binding, endpoint, and authentication policy while retaining typed
offline, timeout, authentication, and trust failures. It neither shells out
for file operations nor copies private-key material into the file subsystem.

Host verification is mandatory unless the durable profile explicitly says `InsecureIgnore`. SFTP passes SSH.NET's raw public-key bytes through the shared Application `ISshHostKeyTrustStore` boundary as a validated `SshHostKeyCandidate`; its SHA-256 display fingerprint is derived from those bytes and cannot be supplied independently. Infrastructure's durable `SshKnownHostStore` implements that boundary for both terminal SSH and SFTP, so a connection ID has one exact algorithm/public-key trust identity and one owner-only, per-connection OpenSSH-compatible file. `Strict` accepts only the exact persisted candidate. `AcceptNew` uses an atomic no-overwrite create, including between separate store instances, and refuses a concurrent or later different key. A changed key can be replaced only through the existing opaque, expiring connection-security review and compare-and-swap action. A malformed or inaccessible trust file fails closed and is never repaired by `AcceptNew`. `InsecureIgnore` is preserved exactly and produces the visible `sftp_host_key_verification_disabled` diagnostic. Host-key bytes and fingerprints are public identity metadata, not credentials.

Unknown and changed host keys remain distinct `FileProviderErrorCode.HostKeyUnknown` and `HostKeyChanged` results through the File Panel boundary. The SFTP provider editor reacts only to those typed results, scans the referenced SSH connection through `IConnectionSecurityRuntime`, and shows the presented and previously trusted fingerprints. Its inline action opens the common modal SSH review; only an explicit **Trust host key** or **Replace trusted key** confirmation for that exact five-minute review can invoke the compare-and-swap and retry the bounded provider test. File Viewer retains the actionable classified message directing the user to that provider review. A malformed trust store remains the separate `HostKeyStoreInvalid` result rather than being presented as an ordinary permission failure. Credential and agent failures remain `AuthenticationRequired`; only a successful authentication followed by a denied filesystem operation becomes `AccessDenied`.

SFTP maps regular-file, directory, link, and POSIX special-file types separately, and retains size, UTC modification time, user ID, group ID, and POSIX mode in an internal metadata snapshot. The common entry exposes type, size, and time; the full snapshot contributes to its opaque conflict token. SSH.NET canonicalizes most path APIs with `REALPATH`, so metadata is resolved from an exact parent listing and the provider walks every configured-root and requested-path component. Links and special files remain visible to list/stat but cannot be read, written, renamed, transferred, or deleted through this adapter.

The reconnect policy may retry list/stat once with a fresh session after a classified transient failure. It never replays a streamed read, write, rename, transfer, or delete: a read retry could duplicate caller output and a mutation retry could repeat a commit whose reply was lost. SFTP stream positions support ranged reads, but the adapter does not advertise `ResumableTransfer`; there is no persisted checkpoint, remote identity revalidation, or safe cross-connection write-resume protocol yet.

### FTP and FTPS

The production FTP transport uses `FluentFTP` pinned to `54.2.0`, commit `928edd5`, under the MIT license. It supplies maintained FTP/FTPS, asynchronous streaming and cancellation, active/passive data channels, encoding control, listing parsers, and FEAT capability discovery.

`FtpTransportSecurity` has only `Plaintext`, `ExplicitTls`, and `ImplicitTls`. Asura never uses FluentFTP's `Auto` mode because that mode may fall back to plaintext. Explicit and implicit FTPS require a completed encrypted control channel, require encrypted data channels, use TLS 1.2 or 1.3, retain platform certificate validation, and fail if TLS is unavailable or the certificate is rejected. Plaintext FTP must be deliberately configured and always exposes the `ftp_plaintext_transport` warning stating that credentials, names, and contents are unencrypted.

Profiles select active or passive data channels and a validated control-channel encoding. Encoding uses exception fallbacks; usernames, remote-root components, caller names, and server-returned names must round-trip before any command is sent. FluentFTP's URL decoding, traversal resolution, control-character truncation, and Unicode-name rewriting are disabled because those transformations could alias a structured provider path to another server path; FTP edge-whitespace names are rejected because FluentFTP trims them unconditionally. The adapter sets encoding explicitly instead of silently changing it after FEAT. Each connection records a non-secret `FtpConnectionSnapshot` containing the completed security mode, encryption state, encoding, and negotiated MLST/SIZE/MDTM/REST/UTF8/checksum flags. A negotiated REST download feature enables efficient ranged reads; without REST the adapter streams from byte zero and discards at most 64 MiB before rejecting a larger offset. REST alone is not safe transfer resume and does not enable `ResumableTransfer`.

FTP passwords are opaque `SecretRef` values resolved with file-provider scope. A negotiated `SIZE` command enriches exact file metadata; reads and transfers fail safely when size remains unknown instead of treating the file as empty. Opaque revisions retain raw listing kind/size and only a timestamp that FluentFTP has positively marked UTC, so list and stat remain stable when `SIZE` enriches an empty file. Server-local LIST times are omitted rather than mislabeled as UTC. These are best-effort conflict tokens, not durable versions, so the provider does not advertise `Versioning` or `AtomicReplace`. Plain FTP and FTPS delete operations are permanent because the protocols provide no portable trash contract. Nonrecursive directory deletion sends `RMD` only after an empty-list check; recursive deletion is driven explicitly by the provider and never calls FluentFTP's implicitly recursive convenience overload.

Both adapters stop retaining a parsed directory after 100,000 entries, and provider cursor state is bounded to 1,024 opaque tokens. SFTP enumeration is streaming. FluentFTP 54.2.0 internally materializes the raw FTP listing before yielding parsed items, so the 100,000-entry cap is not a wire-memory bound for FTP; the adapter disables bulk reads and applies connection/read timeouts, but a future lower-level FTP listing reader is required for a strict raw-response byte cap.

Remote-root and link checks are defensive path checks, not a race-free security sandbox. SFTP v3 and portable FTP lack an `openat`/`O_NOFOLLOW` equivalent that can bind every later path operation to the directory objects just checked; a concurrent remote actor can attempt a check/use ancestor swap, and some FTP servers do not report link identity reliably. Connections crossing a security boundary must therefore use a server-side chroot/jail or a dedicated account whose server permissions already confine it to the intended root. The client checks still prevent ordinary traversal and reported-link following under the expected non-adversarial remote-filesystem mutation model.

### Common remote mutation semantics

Both adapters stream writes and copies into a unique sibling temporary file, validate source length and cancellation, recheck source identity and the destination precondition, then rename into place. When replacing a file, the current destination is first renamed to a unique backup and restored if the commit rename fails. This reduces loss but is not a transactional compare-and-swap across server failures, so neither adapter advertises `AtomicReplace`. A move is copy/commit followed by source delete; failure or cancellation after commit returns `PartialTransfer`. Exact and conservative case-folded aliases are rejected before transfer/rename on provider-defined name semantics.

SFTP and FTP advertise `List`, `Stat`, `RangedRead`, `StreamingWrite`, `CreateDirectory`, `Rename`, `Copy`, `Move`, `Delete`, and provider-local `Pagination`. They do not advertise server-side copy, atomic replace, resumable transfer, durable versioning, checksums, search, watch, symlink creation/following, permissions, or ACL mutation. Directory transfer is also unsupported. SFTP reports case-sensitive POSIX-style names. FTP reports provider-defined comparison because server filesystems vary; callers must not infer case sensitivity from the protocol.

Vendor SDK types are confined to `SshNetSftpSessionFactory` and `FluentFtpSessionFactory`. Deterministic fake sessions exercise both providers through the shared conformance suite without a network, credentials, or live server. Adapter-specific tests cover reconnect bounds, capability honesty, connection identity, restart-persistent and concurrent accept-new trust, explicit changed-key replacement, malformed-store fail-closed behavior, typed host-key UI mapping, plaintext warnings, exact FTPS/data modes, FEAT snapshots, and unsafe FTP name rejection.

An SFTP provider generation retains one authenticated SSH.NET session and serializes its operations. This avoids reconnecting and re-running SSH-agent authentication for every list, stat, preview, and mutation. Retryable transport failures mark the session unhealthy; the existing bounded metadata retry then opens one fresh session. Disposing or replacing the provider generation closes the retained client and releases its authentication material. FTP remains operation-scoped until its adapter has an equivalent explicit health contract.

## Primary sources

- SSH.NET package `2025.1.0`, framework compatibility, repository revision, and MIT license: <https://www.nuget.org/packages/SSH.NET/2025.1.0>
- SSH.NET 2025.1.0 release and async SFTP changes: <https://github.com/sshnet/SSH.NET/releases/tag/2025.1.0>
- SSH.NET SFTP client API: <https://sshnet.github.io/SSH.NET/api/Renci.SshNet.SftpClient.html>
- SshNet.Agent package and OpenSSH-agent integration: <https://www.nuget.org/packages/SshNet.Agent>
- SSH.NET host-key verification example and SHA-256 fingerprint behavior: <https://sshnet.github.io/SSH.NET/examples.html>
- FluentFTP package `54.2.0`, framework compatibility, repository revision, and MIT license: <https://www.nuget.org/packages/FluentFTP/54.2.0>
- FluentFTP source repository and documentation: <https://github.com/robinrodricks/FluentFTP>
- FTP extensions for feature negotiation (`FEAT`): <https://www.rfc-editor.org/rfc/rfc2389>
- FTP over TLS, explicit negotiation, and protected data channels: <https://www.rfc-editor.org/rfc/rfc4217>

## Consequences

- Saved SSH connections and SFTP views share one endpoint, authentication reference, and exact durable host-key identity without duplicating secret material or maintaining a second trust decision.
- FTPS cannot silently downgrade; plaintext FTP remains possible only as an explicit, visibly unsafe profile choice.
- Metadata retry improves recovery without replaying caller-visible bytes or mutations.
- Remote preconditions are useful conflict detection but remain weaker than local atomic replacement or S3 conditional writes.
- Persisted resumable transfer, FTP checksum verification, POSIX permission editing, and directory transfer remain explicit future work.

## Alternatives rejected

- Calling OpenSSH or a system FTP executable for file operations would make
  structured streaming, cancellation, error classification, and cross-platform
  packaging less reliable. The bounded OpenSSH authentication preparation
  described above is deliberately not a file-operation transport.
- Using FluentFTP automatic encryption detection could silently turn a requested secure connection into plaintext.
- Retrying every failed operation could duplicate output or repeat a remotely committed mutation.
- Advertising resume from SFTP seek or FTP REST alone would omit checkpoint persistence, version validation, and safe commit semantics.
- Exposing SSH.NET or FluentFTP objects would couple UI, agent, tests, and future headless clients to vendor APIs.
