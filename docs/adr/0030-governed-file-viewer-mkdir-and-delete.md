# ADR 0030: Governed File Viewer mkdir and permanent delete

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0008](0008-file-provider-contract-and-local-semantics.md),
  [ADR 0012](0012-durable-file-provider-runtime.md),
  [ADR 0019](0019-one-action-agent-capability-broker.md),
  [ADR 0028](0028-governed-file-viewer-observations.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

The governed File Viewer observation boundary is useful for diagnosis, but an
agent also needs a minimal way to prepare a directory and remove one exact
entry. Both actions can change remote state. A delete can be irreversible, and
transport or provider failure after dispatch can leave the application unable
to prove whether the action happened.

The ordinary File Viewer already exposes richer human mutation and transfer
flows. Reusing those UI request shapes would let model data choose provider
identity, mutation preconditions, recursion, retry behavior, or other authority
that must remain trusted.

## Decision

Asura adds two closed governed file mutations:

- `files.mkdir`;
- `files.delete`.

They use the hosted `IFilePanelSession`, its session-pinned provider
generation, and the same broker/SessionHost path as the observations in
[ADR 0028](0028-governed-file-viewer-observations.md). There is no direct
filesystem or provider-SDK path from the agent runtime.

### Model-controlled input

The model supplies only a bounded, typed, non-empty `path_segments` array
relative to the trusted File Viewer root. Exact panel/session schemas contain
only that path. Broad Workspace or internal `OpenTab` schemas additionally
require one `panel_id` selected from the current host-generated eligible-panel enum.

The model cannot supply or replace the session ID, provider profile, authority,
root, absolute location, version, provider capability, mutation precondition,
recursive flag, retry policy, trash policy, or undo behavior. The path
validation and hierarchical/versionless eligibility rules from ADR 0028 still
apply. Neither mutation may target the trusted root.

### Trusted semantics and policy

The trusted composer and SessionHost derive the complete provider request:

- `files.mkdir` uses `CreateDirectory` with `MustNotExist`. It creates the
  exact requested directory only when that entry does not already exist.
- `files.delete` uses `Delete` with `Recursive: false` and `MustExist`. It
  permanently deletes whatever file or empty directory occupies the exact
  approved path at dispatch time.

The delete contract deliberately does not claim observed-object identity. A
replacement at the same exact path before dispatch is the object covered by
the `MustExist` delete. Adding exact version/ETag or observed-entry identity to
the authorization digest requires a later decision.

`files.mkdir` is a mutation and `files.delete` is destructive. `Auto` therefore
escalates before authorization, and SessionHost defensively rejects an
`AutoPolicy` permit. It accepts only exact `HumanApproval` or an explicitly
confirmed, run-local `YoloPolicy` permit. Durable definitions and recovery
never store YOLO. Approval material identifies the exact trusted panel/session,
provider profile and authority, root-relative path, operation, and derived
precondition; delete is presented as permanent and non-recursive.

### Provider eligibility and confinement

Ordinary provider mutation capability is not sufficient to expose either
agent tool. Session metadata carries separate host-trusted
`GovernedCreateDirectory` and `GovernedDelete` flags. They default to absent
and production composition sets them only after the concrete adapter,
transport, and configured root shape have executable confinement and
no-replay evidence. The composer, runtime tool catalog, and SessionHost each
require both the ordinary operation capability and its governed capability.

The current production matrix is deliberately narrow:

| Provider family | Governed mkdir | Governed delete |
|---|---:|---:|
| WebDAV | Yes | No |
| S3/S3-compatible | No | No |
| Local POSIX | Yes | Yes |
| Local Windows, SFTP, FTP, SMB | No | No |

This does not remove ordinary human File Viewer mutations. The POSIX local
adapter opens the configured root and every ancestor directory by descriptor
with `O_NOFOLLOW`, then performs exact non-recursive creation/deletion with
`mkdirat`/`unlinkat`. Namespace replacement cannot redirect the authorized
operation through a symlink between validation and dispatch. Recursive local
delete remains human-only. Windows, SFTP, FTP, and SMB adapters check links or
reparse points before pathname mutation, but those checks are not bound to the
later operation. A concurrent namespace actor can replace an already checked
ancestor, so those adapters are not an authorization boundary against the
same-account/process threat model.

Production WebDAV construction disables redirects and mkdir targets one exact
configured-origin URI. WebDAV delete remains ordinary-only: it must inspect
entry kind before sending DELETE, and a concurrent actor can replace an
inspected file with a collection. WebDAV collection DELETE is recursive, so
that race cannot implement the governed `Recursive: false` promise. S3 uses a
flat object-key namespace, but a key-only delete is permanent only while bucket
versioning is unversioned. Versioning can change concurrently; in an enabled or
suspended bucket the same request can create a delete marker and retain prior
object data. A session-time status check would therefore not justify the fixed
`permanent: true` receipt. No current production provider advertises governed
delete. The closed contract remains implemented and tested for a future
provider with race-free confinement and truthful permanent-delete semantics.

### One dispatch and ambiguous outcomes

SessionHost re-resolves graph ownership, session identity and revision, the
immutable root/profile/authority binding, and both session and provider
capabilities immediately before consuming the one-action permit. Cancellation
or binding drift detected before provider invocation is a definite failure and
no mutation is called.

The runtime submits each accepted action to SessionHost once, and SessionHost
invokes the captured provider mutation exactly once. There is no action-level
retry at either boundary. Once the provider invocation begins:

- a valid success receipt wins over late caller cancellation, permit
  revocation, or graph/session drift;
- a definite typed provider rejection such as access denied, already exists,
  or precondition failed becomes a bounded failed tool result and is returned
  to the provider for recovery without redispatching the action;
- an ambiguous transport/cancellation failure, exception, or malformed,
  mismatched, wrong-kind, or out-of-root receipt becomes the non-retryable
  stable failure `file_mutation_outcome_unknown`;
- that ambiguous result is audited as `Failed`, never `Cancelled`;
- the runtime commits that result, skips the stale remainder of the current
  tool batch, and returns control to the provider so it can inspect fresh file
  state before choosing another action.

The provider operation is never redispatched to discover or repair an unknown
outcome. Only the exact immutable completion-audit event may use the broker's
existing bounded reconciliation path.

For S3 and S3-compatible profiles, the production store represents the
one-object delete as a one-key `DeleteObjectsAsync` POST. `MustExist` is carried
as the supported per-object `ETag` value `*`; embedded per-object failures
remain failures. Asura deliberately does not use the superficially
simpler single-object DELETE because `SocketsHttpHandler` may transparently
replay that idempotent verb after a response-less disconnect. The conditional
multi-object form is documented by
[Amazon S3 conditional deletes](https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-deletes.html).

The production SDK configuration also sets both `MaxErrorRetry` and
`MaxStaleConnectionRetries` to zero. A bounded loopback transport test proves
that both a valid 503 response and a literal zero-response-byte disconnect
produce exactly one fully received one-key POST. The test inspects the wire
request; application method-call counts alone are not accepted as replay
evidence.

That S3 transport hardening remains valuable for ordinary File Viewer delete,
but does not grant `GovernedDelete`. AWS documents that a delete without a
specific version ID is a soft delete in versioned buckets and permanent only
in unversioned buckets:
[Deleting Amazon S3 objects](https://docs.aws.amazon.com/AmazonS3/latest/userguide/DeletingObjects.html).

Production WebDAV MKCOL and DELETE requests carry an explicit zero-length
content body. This prevents the HTTP transport from treating their otherwise
contentless requests as transparently replayable after a response-less
disconnect. Bounded loopback tests fully receive each request, close without
returning a response byte, and prove one request rather than the four sends
observed without the explicit body. Only MKCOL is governed in the current
provider matrix; ordinary WebDAV delete retains the same transport hardening.

### Hostile receipts and provider projection

Provider receipts are untrusted. A mkdir receipt must identify the exact
requested location inside the trusted root, with the exact requested final
name and directory kind. A delete receipt must identify the exact requested
location inside the trusted root. Accepted locations are reconstructed from
trusted request material with provider versions removed; provider-reported
delete kind is not authority.

The runtime returns only fixed, metadata-free receipts:

- exact scope: `{"ok":true,"created":true}` or
  `{"ok":true,"deleted":true,"permanent":true}`;
- broad scope: the same receipt plus the already trusted selected `panel_id`.

No path, profile, authority, version, entry metadata, provider message, trash
token, undo token, or provider-controlled stable code reaches provider
continuation or durable audit.

### Deliberate exclusions

This slice has no recursive delete, trash, undo, restore, rename, copy, move,
write, upload, transfer, batch mutation, replace-existing mkdir, root delete,
or model-controlled retry. Those operations require separate authority and
conflict semantics.

## Consequences

- The agent can create one exact WebDAV or local POSIX directory without
  receiving ambient filesystem authority, and can permanently delete one exact
  non-recursive local POSIX entry through descriptor-relative mutation.
- The approval and dispatch cannot be widened by model-supplied provider
  identity, recursion, precondition, or retry fields.
- A caller cannot safely redispatch an ambiguous mutation. The live run remains
  usable, but it must inspect current provider state before choosing a new
  action.
- `MustExist` protects against deleting an already absent path, but it does not
  protect the identity of an entry replaced at the same path.
- Ordinary human mutation capabilities remain available on providers that do
  not yet meet the stricter governed confinement contract.
- Provider-specific hidden retries are part of the security boundary and need
  transport-level evidence, not only mock invocation counts.

## Alternatives rejected

- Reusing the ordinary File Viewer mutation request would expose trusted
  preconditions and operation flags to model data.
- Treating provider failure or cancellation after invocation as a safe retry
  could duplicate an irreversible remote effect.
- Recursive delete, trash fallback, or automatic retry would silently change
  the exact approved semantics across providers.
- Requiring a prior stat without binding its version into the action would
  create a false impression of observed-object identity.
