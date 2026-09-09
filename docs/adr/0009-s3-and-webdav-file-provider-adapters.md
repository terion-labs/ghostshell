# ADR 0009: S3 and WebDAV file-provider adapters

- Status: Accepted
- Date: 2026-07-22

## Context

ADR 0008 defines a provider-neutral file API with bounded reads and open-ended streaming transfers. S3 is an object store whose legal keys are not hierarchical paths; WebDAV is a hierarchical HTTP namespace whose mutation and destination-precondition rules differ from ordinary HTTP. Both adapters must preserve caller-owned streams, cancellation, opaque versions, and typed conflicts without requiring live credentials in the test suite.

## Decision

### S3 and S3-compatible services

The production transport uses the official `AWSSDK.S3` package pinned to `4.0.101.3`. This was the latest stable version on 2026-07-22 (published 2026-07-17), targets .NET 8 and later, and is licensed under Apache-2.0. The package is maintained by AWS, implements Signature Version 4, and exposes the current destination `IfMatch`/`IfNoneMatch` conditional-write APIs. Asura accepts a caller-configured `IAmazonS3`; credential discovery, region, endpoint URL, TLS policy, and path-style addressing therefore remain profile/bootstrap concerns and can support AWS plus S3-compatible endpoints without storing secrets in file locations. The provider does not dispose that client.

The container root is structurally distinct from an object. `FileObjectKey` maps byte-for-byte to an S3 key, including repeated or trailing `/` and `.`/`..` data. A hierarchical location is available for delimiter-based browsing of ordinary keys. ETags are opaque `FileVersion` values and are never interpreted as MD5 hashes. `ListObjectsV2` supplies bounded pages; because service continuation tokens can exceed the provider-neutral token limit, a bounded provider-local cursor maps them to 32-character opaque tokens and validates their bucket/prefix scope.

Reads use one conditional byte-range `GetObject`. Writes use one length-declared `PutObject` with `If-None-Match`, `If-Match: *`, or an ETag as required. Same-bucket copies use `CopyObject` with both source and destination ETag conditions and advertise `ServerSideCopy`. Conditional delete uses `DeleteObject.IfMatch`. The adapter advertises only `List`, `Stat`, `RangedRead`, `StreamingWrite`, `Copy`, `Delete`, `ServerSideCopy`, and `Pagination`.

Portable S3 rename/move is not advertised: copy followed by delete can commit only one side, while the newer `RenameObject` operation is not available across general-purpose S3 and compatible services. Object buckets do not advertise directory creation. Recursive prefix deletion is rejected instead of expanding one user operation into an unbounded destructive batch. The adapter also does not advertise `AtomicReplace`, `Versioning`, resumable transfer, watch, search, permissions, ACLs, checksums, or symlinks. The current adapter issues one PUT or COPY operation and does not invent a smaller application ceiling; service-specific object or single-operation constraints remain typed provider failures. Multipart/resume remains a separate capability because it needs durable checkpoints and commit semantics.

### WebDAV

The WebDAV transport uses the platform `HttpClient` and RFC 4918 directly rather than adding a protocol wrapper package. RFC 4918 fully specifies the methods needed here, while `HttpClient` provides maintained HTTP/TLS, streaming, cancellation, and authentication-handler integration. The caller owns the client and configures credentials; its primary handler must disable automatic redirects so credentials or custom headers cannot be sent before the provider can validate a redirect target. The provider also validates every final response URI as defense in depth. The base URI rejects embedded user information, query strings, and fragments. Every request URI is assembled from individually escaped path segments below that base, and every response `href` is checked against the configured origin and base path. XML DTD processing is prohibited.

`PROPFIND` depth 0 implements stat; depth 1 implements list. Property bodies are capped at 8 MiB and 10,000 entries. Since RFC 4918 defines no collection pagination, the adapter exposes bounded client-side pagination over one immutable property snapshot; continuation cursors are provider-local, scope-checked, and limited to 32 retained states. A syntactically valid strong `getetag` is required because safe conditional reads and mutations cannot be represented with a weak or missing validator.

Ranged reads issue `Range` plus `If-Match`, require a matching `Content-Range` on 206, and request identity encoding. A 200 response is accepted only for an offset-zero prefix; a server that ignores a nonzero range is rejected instead of downloading an unbounded prefix merely to skip it. PUT uses standard HTTP conditional headers and an exact-length caller-stream view. MKCOL creates collections. RFC 4918 COPY and MOVE use `Destination`, `Overwrite`, and tagged `If` lists so both source and destination ETags participate in the operation; COPY therefore advertises `ServerSideCopy`. DELETE of a collection is recursive under RFC 4918 and is sent only when the caller explicitly requests recursion. A shallow collection delete is rejected because a list-then-delete sequence has an unavoidable membership race. Provider-local WebDAV transfer currently accepts files, not recursive collections; cross-provider directory orchestration is owned by the transfer queue.

The WebDAV adapter advertises `List`, `Stat`, `RangedRead`, `StreamingWrite`, `CreateDirectory`, `Rename`, `Copy`, `Move`, `Delete`, `ServerSideCopy`, and `Pagination`. It does not advertise `AtomicReplace`, `Versioning`, resumable transfer, watch, search, permissions, ACLs, checksums, or symlinks. Cancellation stops local request/response processing, but a remote server may have committed a request before observing transport cancellation; expected remote uncertainty is why neither remote adapter claims `AtomicReplace`.

## Error and test policy

HTTP/S3 expected failures map to stable provider errors: authentication/authorization to `AccessDenied`, 404 to `NotFound`, conditional 412 to `Conflict` or `PreconditionFailed` according to the requested precondition, range 416 to `RangeNotSatisfiable`, DAV 423 to `SharingViolation`, DAV 507 to `QuotaExceeded`, multi-status mutation failure to `PartialTransfer`, and throttling/server failures to retryable `IoFailure`.

No test requires cloud credentials or network access. S3 semantics run against a narrow deterministic object-store fake behind the real AWS SDK adapter boundary. WebDAV semantics run through a stateful fake `HttpMessageHandler`, which verifies actual PROPFIND/GET/PUT/MKCOL/COPY/MOVE/DELETE requests. Both providers run the shared capability conformance suite, and adapter-specific tests verify exact S3 keys, oversized continuation tokens, server-side copy paths, bounded short writes, and safe delete restrictions.

## Primary references

- AWS SDK for .NET S3 package `4.0.101.3`, compatibility and Apache-2.0 license: <https://www.nuget.org/packages/AWSSDK.S3/4.0.101.3>
- AWS SDK for .NET v4 `PutObjectRequest` conditional properties: <https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TPutObjectRequest.html>
- Amazon S3 conditional-write behavior: <https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html>
- Amazon S3 `CopyObject` source/destination conditions: <https://docs.aws.amazon.com/AmazonS3/latest/API/API_CopyObject.html>
- AWS SDK for .NET v4 `ListObjectsV2Request` pagination and bounds: <https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TListObjectsV2Request.html>
- RFC 4918, HTTP Extensions for Web Distributed Authoring and Versioning: <https://datatracker.ietf.org/doc/html/rfc4918>
- RFC 9110 range and partial-response semantics: <https://www.rfc-editor.org/rfc/rfc9110.html#name-range-requests>
- .NET automatic redirect behavior: <https://learn.microsoft.com/dotnet/api/system.net.http.httpclienthandler.allowautoredirect>

## Consequences

- The UI and agent can browse and preview these providers through the same typed contract without flattening exact S3 keys into unsafe paths.
- Concurrency failures are visible and recoverable instead of silently overwriting newer data.
- Server-side copies avoid downloading data through Asura, while source identity and destination preconditions are checked before the operation.
- Multipart S3 transfer, recursive WebDAV collection transfer, and S3 prefix deletion remain deliberate future features rather than partially safe implicit behavior.
