# ADR 0043: Idle native-agent checkpoints

- Status: Accepted
- Date: 2026-08-13

## Context

The native agent kernel retains a bounded, stable conversation in memory, but
process exit currently loses it. Pi's durable harness is a useful reference for
the distinction between settled transcript state and volatile provider/tool
effects. Asura does not implement Pi's durable operation interpreter,
effect-intent log, or branching lanes. Resumable recovery is deliberately
limited to the last fully committed idle conversation, but visible chat
history must not roll back to an older turn when a process exits mid-request.

Persisting the kernel's entire object graph would be unsafe. An active provider
stream is incomplete and may still commit or fail. A pending tool proposal is
an approval decision, not settled conversation. Provider adapters, capability
leases, approval principals, policy authorities, and resolved credentials are
process-local capabilities and must never become session data.

## Decision

`NativeAgentSession.CaptureCheckpoint` captures a checkpoint only while the
session is `Ready`, with no provider operation, pending tool decision, or
compaction lease. The checkpoint contains the run ID, schema version,
generation, event revision, conversation revision, last sequence, last settled
tool generation, optional generated conversation title, complete stable
provider context projection, complete committed user-visible transcript, the
last trusted provider/model route used for history
attribution, and deterministic provider-tool alias bindings. Stable assistant
reasoning summaries and provider token usage are part of the conversation.
Provider-private replay state may also round-trip
when it contains only signed/summarized Anthropic blocks, opaque redacted
blocks, or encrypted/finalized OpenAI Responses items. The payload stores those
bounded JSON items as base64 so credential-property scanning cannot confuse
opaque provider field names with executable configuration. The exact provider,
protocol, model, routed endpoint, and adapter/auth route binding is restored
with item order and tool slots. A future continuation must match all of it
before replay. Bounded user images retain only their plain file
name, verified media type, and copied bytes; restore reconstructs them through
the same signature-validating attachment constructor. No provider client, tool
definition, approval, policy authority, capability, secret reference, or
resolved secret is in the format.

`CaptureInterruptedCheckpoint` uses the same bounded document for history-only
recovery. Before provider invocation it stores the accepted user message plus a
fixed interruption receipt. At every pending proposal it excludes the
unexecuted proposal; after a completed governed tool batch it retains the exact
structured results and closes the transcript with the same fixed receipt. Each
capture advances the kernel event revision before the SQLite compare-and-swap.
The payload state is `interrupted`, and restore produces a `Ready` native
session with no provider operation, proposal, approval, permit, capability, or
run authority. It can be displayed and continued with a new user turn, but it
cannot be mistaken for automatically resumable execution state.

The current schema-v3 payload is owned by `Asura.Agent`; storage treats it
as an opaque bounded JSON object. Schema v2 added the optional bounded generated
title. Schema v3 separates the append-only committed transcript from the
compacted provider context projection. Restore still accepts schema v1/v2,
using their single conversation as both values because already-discarded
pre-compaction messages cannot be reconstructed. Schema v1 also derives the
deterministic first-user-message title. Restore rejects unknown fields, unsupported newer
schema versions, inconsistent revisions/generations, malformed conversation shapes,
duplicate or changed provider aliases, and values outside the current kernel
limits. Credential-shaped literal text and structured credential properties
fail checkpoint capture and restore instead of becoming durable data.
Provider raw reasoning that was intentionally suppressed from user-visible
reasoning summaries can exist only in volatile memory for its immediate tool
continuation. This includes Anthropic thinking and OpenAI reasoning text;
encrypted OpenAI continuity data remains eligible. Checkpoint capture keeps the
committed visible conversation but removes an assistant message's entire
provider-private replay state when that state contains suppressed raw reasoning.
The restored transcript therefore remains readable and continuable without
making hidden reasoning durable or replaying a partially stripped provider atom.
Restore decodes replay bytes with strict UTF-8, derives the unsafe
classification from the item kinds instead of trusting the serialized
compatibility bit, and revalidates each item's canonical provider shape against
the committed visible text, reasoning summary, and tool proposals. Hidden
chain-of-thought is therefore never made durable or smuggled through a
divergent replay transcript.

`IAgentSessionCheckpointStore` is the application port. Its SQLite adapter
stores one row per agent run, bound to its live workspace identity, including
redundant schema/generation/revision metadata and a SHA-256 digest covering the
workspace identity, run identity, metadata, and payload. Saves
run in an immediate transaction and compare the stored revision before an
upsert. An identical same-revision save is idempotent; a different or older
same-run save fails with a revision conflict. Load validates row types, size,
canonical UTC time, JSON shape, and the digest before returning the opaque
checkpoint. Delete and bounded newest-first list complete the initial durable
surface.

Migration 11 creates the dedicated checkpoint table. Migration 14 adds the
workspace binding and its newest-first index; pre-migration rows remain
unscoped and are not projected into any live workspace. Both migration
receipts are frozen alongside the existing historical SQLite fixtures.

## Consequences

The durable unit is a settled snapshot or an inert interrupted transcript, not
an external-effect journal. A provider request or tool action active at process
death is never reconstructed. Completed tool results already captured remain
visible; a pending proposal is absent. A normal checkpoint restores idle and
resumable, while an interrupted checkpoint restores as a closed, sendable
transcript and cannot infer or replay unfinished work.

Compaction is invisible to chat history. It updates only the bounded provider
context projection (system messages, the internal summary, and retained whole
turns). The user-visible transcript keeps every committed message and is the
source for rendering, titles, forks, and history scrollback. The internal
summary is never projected as a user or assistant message.

The desktop creates one `GovernedAgentRuntime` for every live workspace,
including Quick Terminal's independent workspace. Each runtime saves every
accepted initial or queued user message before its provider invocation,
advances the interrupted checkpoint across tool rounds, commits a completed
provider transcript before title generation or compaction, and replaces it
with the final resumable checkpoint after the fully completed provider/tool
turn. It loads only the newest valid checkpoint from
its own workspace when its chat is created. Switching
workspaces switches the active runtime and transcript; no active chat or
history catalog crosses that boundary. No pending
approval, queued prompt, provider request, tool action, or capability is
resumed. The first new prompt lazily rebinds the restored kernel session only
when the trusted current workspace manifest is identical; provider replay also
requires its original profile, protocol, model, endpoint, auth route, and vault
reference binding. A mismatch fails closed and requires Clear. Clear deletes
the durable checkpoint before resetting the visible run. If safe capture or
storage fails, the in-memory completed response remains visible and the UI
reports that local saving failed.
