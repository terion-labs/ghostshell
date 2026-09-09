# ADR 0049: Governed file text and copy mutations

**Status:** Accepted
**Date:** 2026-08-26

## Context

The first governed File Viewer mutation boundary covered directory creation,
move/rename, and delete. It could not create or replace file content, and the
ordinary transfer queue was unsuitable for agent copy: it is asynchronous,
retryable, and has no synchronous terminal receipt that proves one exact
commit.

Provider versions also could not be returned directly to a model. They are
provider-controlled values and can contain sensitive or unbounded material,
but replace, copy, move, and delete need to act on the exact entry that was
observed.

## Decision

Asura adds three closed tools:

- `files.create_text` creates one non-root path with `MustNotExist`;
- `files.replace_text` replaces one regular file with `VersionMatches`;
- `files.copy` copies one regular file within one hosted provider profile to a
  distinct non-root path with `MustNotExist`.

Text is strict UTF-8 and is limited to 8 KiB. Copy is synchronous, limited to
64 MiB, accepts only a regular source with a known size, and does not use the
ordinary transfer queue. Tool results contain only a fixed completion receipt;
they never return content or provider versions. Cancellation or an exception
after mutation dispatch produces the non-retryable
`file_mutation_outcome_unknown` result.

`files.stat` now returns a 256-bit opaque `entry_ref`. SessionHost retains the
provider version in memory and binds the reference to the run, exact panel,
session, session revision, relative path, kind, size, and a five-minute
expiry. The pool is bounded to 256 entries. Replace, copy, move, and delete
must present the reference; SessionHost validates it before authorization and
consumes it exactly once after the one-action permit is consumed. Raw provider
versions never enter model-visible JSON, approval text, or durable agent
material.

Approval binds the exact paths, opaque reference, UTF-8 byte count, and content
SHA-256. It does not display or persist content in approval material. Text with
credential-shaped literals or unsafe control/format characters is rejected.

## Provider advertisement

The capability flags are separate from ordinary streaming-write and copy
support. A profile advertises them only after executable confinement,
precondition, receipt, and non-replay evidence.

Configured WebDAV profiles advertise governed create, replace, copy-source,
and copy. WebDAV resolves every URI below the configured base, disables
redirects, sends conditional PUT/COPY requests, and uses an explicit request
body so `SocketsHttpHandler` does not replay a response-less mutation. Loopback
tests drop the response after fully receiving PUT and COPY and assert that each
mutation is dispatched once.

The macOS/Linux local provider does not yet advertise these new flags. Its
mkdir and shallow-delete boundary traverses parents with descriptor-relative
`openat`/`mkdirat`/`unlinkat`, but text-write and copy still validate a string
path and later commit with `File.Move`. A parent directory can be replaced
between validation and commit. Advertising local create/replace/copy therefore
waits for descriptor-relative commit operations and adversarial parent-swap
tests. Windows, S3, SFTP, FTP, and SMB also remain unadvertised for this family.

## Explicit exclusions

- Cross-profile upload/download and cross-panel transfer are not represented
  by `files.copy`; they require a two-session authorization and terminal
  receipt design.
- Directory copy is excluded.
- Transfer cancellation and retry remain ordinary human operations.
- ACL mutation is excluded because current providers do not offer a
  provider-neutral race-safe compare-and-swap setter.
- Binary replacement and model-provided host paths, URLs, object keys, or
  ambient upload artifacts are excluded.

## Consequences

The model can perform bounded file-content work on provider configurations
that prove the required semantics. Stale or replayed observations fail closed,
and uncertain commits are never retried. Providers with ordinary write/copy
support remain unavailable to these tools until their governed capability is
separately attested.
