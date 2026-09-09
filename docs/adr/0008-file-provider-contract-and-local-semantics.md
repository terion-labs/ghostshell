# ADR 0008: Structured file-provider contract and local filesystem semantics

- Status: Accepted
- Date: 2026-07-22

## Context

Asura must present local filesystems and S3, SFTP, FTP, SMB, and WebDAV through one file panel without claiming that those backends share path, version, conflict, or transfer semantics. Provider targets are security boundaries: a local-root escape or a location assembled by string concatenation would widen both human and agent access. Preview and agent reads must remain bounded when a provider contains very large objects, while user-initiated transfers must stream without an arbitrary application-level file-size ceiling.

## Decision

`Asura.Files` owns the provider-neutral contract and real provider adapters. A `FileLocation` consists of a provider profile ID, optional authority/container, a discriminated address, and an optional opaque provider version. The address is either immutable hierarchical path segments, an exact opaque object key, or a distinct container root. This preserves object keys containing repeated/trailing delimiters and dot segments without interpreting them as traversal. Callers can append a validated segment only to hierarchical locations and cannot turn a provider location into a local path. Native root paths remain private adapter configuration.

`IFileProvider` exposes typed list, stat, ranged-read, streaming-write, create-directory, rename, transfer, and delete operations. Expected failures return stable `FileProviderErrorCode` values. Mutations carry an explicit existence or version precondition. Ranged reads declare a maximum byte count; writes declare their exact content length; all streaming operations declare a bounded buffer size. Providers advertise read, page, and buffer limits plus optional capabilities. Copy, move, and streaming write have no Asura size ceiling: storage capacity, quota, object-size, and protocol failures come from the selected provider instead of a guessed common denominator. A provider may advertise an optional capability only after its implementation passes the shared conformance suite for that behavior.

The local adapter is selected by host platform. POSIX and Windows adapters share confined filesystem behavior while applying their own name and case rules. macOS reports case behavior as provider-defined because APFS volumes can be either sensitive or insensitive. Path traversal segments are invalid by construction. The configured root and all traversed parents must not be symbolic links, junctions, or other reparse points. A leaf link is visible through list/stat and can be deleted as a link, but is never followed for read, write, navigation, rename, or transfer.

Local writes and copies stream into a sibling temporary entry, recheck the destination precondition, and rename only after completion. This is the meaning of the local `AtomicReplace` capability; it does not imply transactional multi-file updates or strongly atomic compare-and-swap. Local versions are opaque metadata change tokens, not durable object versions, so the adapter does not advertise `Versioning`. A move is a staged copy followed by deletion; failure after destination commit returns `PartialTransfer` explicitly.

S3, SFTP, FTP, SMB, and WebDAV will use maintained libraries or platform APIs behind the same contract. Each adapter must document authentication, cancellation, pagination, version/precondition mapping, resume guarantees, and capability claims before it is enabled.

## Consequences

- UI and agent code can authorize exact provider prefixes without parsing or joining local path strings.
- Local cancellation before commit leaves an existing destination unchanged.
- Link and Windows reparse-point checks prevent non-concurrent traversal from
  silently widening a configured root. They are not a sandbox against a
  same-account process that races an ancestor replacement, so pathname-based
  local mutations are not advertised as governed agent mutations.
- The conformance suite fails when an adapter advertises behavior that has no executable contract test.
- Cross-provider transfer orchestration and provider-profile persistence remain application slices built on this boundary.
- Directory replacement is not advertised as atomic; callers must use a conflict flow instead.

## Alternatives rejected

- URI strings obscure provider identity, separator rules, and opaque versions and invite unsafe concatenation.
- A generic `Execute(string, object)` operation would erase typed errors, limits, and capability negotiation.
- Following links after a prefix check remains vulnerable to root escape and provider-specific reparse behavior.
- Buffering whole files or objects in memory cannot satisfy bounded preview or open-ended transfer requirements.
- Implementing network storage protocols directly would create avoidable security, interoperability, and maintenance risk.
