# ADR 0017: Native .NET agent runtime

- Status: Accepted
- Date: 2026-07-23
- Supersedes: [ADR 0005](0005-agent-sidecar-and-capability-broker.md)

## Context

The Pi project remains a useful reference for agent-session lifecycle, provider
streaming, steering, compaction, and tool calls. Running it in Asura would,
however, require a Node.js child process solely for the built-in agent. That
would add another runtime, package supply chain, process supervisor, IPC
protocol, upgrade path, failure domain, and idle memory cost to every desktop
installation.

Desktop v1 already runs its session host in-process behind protocol-shaped
application contracts. Provider/model flexibility does not require the agent
loop itself to live in another process, and process isolation would not replace
the capability checks that must occur at the session-host execution boundary.

## Decision

Implement the first agent runtime natively in .NET. The loop is a real
`Asura.Agent` boundary that owns conversation state, provider streaming,
tool-call assembly, steering, compaction, and run cancellation. It depends on
provider-neutral Core primitives, not Avalonia controls, terminal engines,
provider SDK payloads, or persisted vendor session formats.

Provider adapters parse each provider's external stream into the native loop's
small typed event model. Provider credentials are resolved from `SecretRef`
only inside the provider-adapter boundary and are never added to the
conversation or tool results. Official provider SDKs or direct HTTP clients may
be used privately when they reduce compatibility risk; their request and
response types do not cross the adapter.

Provider-native reasoning continuity is retained through one internal bounded
replay-state value on the committed assistant message. An adapter may emit that
value only once, immediately before successful response completion; the
provider-neutral reducer never projects it to run events, UI, logs, or audit.
The binding covers the exact profile ID, provider identity, wire protocol,
model, actual routed endpoint, and stable adapter/auth route identity. For
vault-backed routes, that identity contains a one-way digest of the selected
opaque credential reference, never the reference itself or its value. Any
endpoint, credential-reference, authentication-route, profile, or protocol
drift fails closed before the transcript is serialized or sent. Replacing
material behind the same opaque reference is outside this check because the
current vault contract exposes no immutable credential revision.

A conversation is identified by its workspace-scoped run, not by its model.
The model is a per-turn routing choice: changing it while idle retains the
committed visible transcript and does not clear or fork the conversation. The
next request uses the selected model under the already-pinned provider profile
and authorization policy. Exact-model replay may retain provider-private signed
or encrypted reasoning artifacts. A same-route model change instead serializes
the visible assistant text and tool history without those model-bound opaque
artifacts, matching Pi's cross-model message transformation. This preserves the
human conversation without presenting incompatible provider state to the new
model.

Conversation maintenance follows Pi's context-budget behavior without adopting
its session format. Every model descriptor may publish a bounded context
window. After a successful turn, the kernel uses the latest provider-reported
total plus bounded estimates for any trailing messages, and compacts when that
usage exceeds `contextWindow - 16,384`. It retains approximately 20,000 tokens
of the newest complete user turns, summarizes only the older complete turns,
and rolls an existing summary forward. Asura never splits a structured
tool exchange merely to hit the token target. The summarizer receives a
prompt-injection-resistant structured checkpoint contract derived from Pi's
Goal, Constraints, Progress, Decisions, Next Steps, and Critical Context
sections. Compaction remains revision-fenced and optional maintenance: a
maintenance-provider failure cannot discard the answer that already completed.
Like Pi's append-only session log, the workspace transcript and the provider
context projection have separate lifecycles. Compaction replaces only the
projection sent to a provider. The complete committed user-visible transcript
remains append-only, is restored from its checkpoint, and never displays the
internal summary as a chat message.

The compaction route and optional conversation-title route are independent
provider/model selections in the global AI configuration. Workspace and saved
screen policy layers may override either route independently. An unspecified
compaction route uses the resolved global primary model; an unspecified title
route keeps the deterministic first-user-message title and performs no extra
provider request. Main-window and Quick Terminal conversations both consume
the saved global policy while retaining separate workspace-scoped transcripts.
The visible composer projects current usage and the active
model's effective context budget. These maintenance routes are data-processing
choices, not execution authority, and never receive agent tools.

Anthropic adapters retain the exact ordered signed `thinking`, opaque
`redacted_thinking`, text, and tool-use blocks so a tool result can immediately
continue a signed turn without exposing hidden thinking. OpenAI Responses
requests `reasoning.encrypted_content`, retains finalized response items and
their output/tool slots, and backfills encrypted reasoning from the completed
response when the output-item event omitted it. Both formats enforce strict
item, aggregate byte, JSON-depth/node, slot-contiguity, and duplicate bounds.
Other adapters receive no replay-state surface.

Internal Asura operation names remain stable domain and audit identities;
they are not assumed to satisfy a model provider's tool-name grammar. Each tool
definition therefore carries a separate provider name limited to 64 ASCII
letters, digits, underscores, or hyphens. Already compatible names, including
run-local MCP aliases, remain unchanged. Other internal names receive a
deterministic collision-resistant opaque alias. The reducer accepts only the
exact provider-name map frozen for that turn and translates a returned call
back to its internal operation before any proposal reaches orchestration.
Provider request history replays the exact retained alias. A bounded
session-owned alias ledger rejects any attempt to bind an alias to a different
internal operation across later turns, tool continuations, cancellation, or
compaction. Malformed Unicode, per-manifest collisions, cross-turn rebinding,
and session-capacity overflow all fail before provider invocation.

The loop cannot execute application tools. A model tool call is an untrusted
proposal correlated to an authenticated agent run. Asura resolves its
exact target, policy, risk, approval, and one-action authorization before the
session host invokes a typed application operation. The host records the
requested decision and terminal outcome durably. Provider and run cancellation
share one whole-turn boundary. Each authorized tool dispatch additionally owns
a linked one-action cancellation boundary; cancelling only that action returns
a structured cancelled tool result and does not revoke the run.

Desktop v1 exposes no agent IPC endpoint and grants no ambient authority to
other same-account processes. A future standalone or headless host must add an
authenticated transport and its own identity ADR without weakening the same
target, policy, approval, audit, and execution contracts.

Pi remains a behavior and test reference only. Asura does not package
Node.js, launch Pi, consume Pi session files, or depend on TypeScript types.

The foundational loop deliberately owns no provider transport or tool
authority. `Asura.Agent` references only Core primitives and the BCL. It
implements strict bounded stream reduction, stable transcript validation,
generation-fenced cancellation, bounded non-cooperative provider work,
CAS-based compaction, cursor resynchronization, cloned data-only tool
proposals, and structured tool-result continuation. Cancelling pending
proposals rolls their unexecuted turn back. Provider-turn budgets distinguish
at most 128 advertised tool definitions from at most 16 returned calls, and
bound schemas independently from generated call arguments.

[ADR 0043](0043-idle-native-agent-checkpoints.md) adds a deliberately narrower
durability boundary than Pi's operation harness. Fully committed `Ready`
conversation state is resumable. An in-progress turn is stored only as an
inert, valid transcript marked `interrupted`; it restores as a closed,
sendable transcript with no proposal or authority. Active provider streams,
pending tool decisions,
compaction leases, approvals, capabilities, authorities, provider clients, and
credentials remain process-local. A versioned kernel-owned JSON payload is
stored behind an application port by a revision-fenced, integrity-checked
SQLite adapter. Restore never infers or replays an unfinished external effect,
and desktop orchestration restores only within the same workspace identity.

[ADR 0046](0046-ordered-step-boundary-agent-steering.md) defines the current
desktop steering contract. Human input enters a bounded, editable workspace-run
queue. Ordinary follow-ups run when the agent would otherwise stop; steering
runs first at the next settled provider or complete tool-batch boundary. The
kernel records it as a distinct user turn instead of cancelling or rewriting
the active generation. Queue input creates no permit, SessionHost operation,
authority decision, or audit row. The earlier generation-replacement primitive
from [ADR 0037](0037-bounded-native-provider-steering.md) is not routed by the
desktop presentation.

[ADR 0018](0018-native-ai-provider-and-chat-boundary.md) adds provider I/O in a
separate native project. Secret resolution remains request-local to that
adapter. A provider profile is pinned by immutable catalog revision for an
agent run; editing, disabling, or removing it invalidates the binding before
any retained transcript can be sent again.

`Asura.Agent.Runtime` is the provider-neutral orchestration boundary. It
references the agent kernel plus application contracts, but no provider,
terminal-engine, platform, vault, or UI implementation. The desktop binds a
run to one workspace identity. A bounded host-generated system manifest is
assembled by the runtime's registered tool-family contributions and rebuilt
after each tool round from the current supported live panels. The current
registry is runtime-owned; panels contribute eligibility and current context,
not executable plugin objects. Panel titles, connection
labels, and working directories remain explicitly untrusted; every operational
schema narrows authority with a host-enumerated `panel_id` where needed. For
every proposal the runtime freshly resolves the target and selected panel,
converts the request into an exact panel/session action, waits for the capability
broker, invokes the session-host consume-and-execute bridge, redacts and bounds
the result, and returns the trusted panel ID with the correlated structured
result to the same request-scoped provider adapter. Returned tool proposals are
executed sequentially in provider order and submitted as one correlated result
set. Tool results are committed as their own stable transcript boundary before
another provider request begins. An uncertain effect is returned as a
non-retryable failed tool result; the remainder of that already-planned batch
is settled as `tool_batch_reconciliation_required` instead of executing against
possibly changed state. The provider may then inspect fresh state and recover.
Only authority/audit integrity failures quarantine the whole run.
Exact internal targets retain fixed-membership fail-closed semantics. Provider
continuation is not capped by a tool-round count or a whole-turn deadline; long
workflows end when the provider completes or the user cancels them. Each
governed action remains independently bounded. A linked, identity-tracked
cancellation source exists
only while one authorized panel dispatch is active. The same activity snapshot
carries the host-selected exact panel identity. The desktop acquires a
turn-scoped panel-presence lease from that identity, retains it while the
provider reasons between tool calls, transfers it when a later action selects a
different panel, and releases it when the turn becomes ready, failed, or
cancelled. Tab and workspace markers derive from that lease even when the
workspace is not in front. The one-shot action-cancellation identity still
clears before disposal, so stale completion and duplicate-cancel races cannot
affect a later action. Whole-turn cancellation takes precedence and revokes run
authority.

The settlement boundary also permits checkpointing and PI-style conversation
compaction between tool rounds. A large tool workflow therefore summarizes old
completed turns before constructing the next provider request rather than
waiting until the provider has ended the entire workflow.

That same current ordered Workspace topology is projected as immutable
presentation-only rows for the visible desktop context inspector. The snapshot
is replaced after each successful topology refresh; internal exact/selected
targets retain fixed rows. Exact identities and host-verified operations remain
distinct from bounded, redacted, explicitly untrusted display metadata. The
rows are cleared with the run and cannot be consumed as authorization.

If execution finishes but the terminal-outcome audit remains unavailable after
the host's exact-completion retry, the runtime never submits that result for
provider continuation. It cancels the run and reports the stable recovery
failure instead. Retrying a completion cannot redispatch its terminal side
effect.

The desktop binds that runtime to one composition-owned human approval
principal and presents a visible `Workspace` scope, streaming state, active
operations, one-action
approvals, per-action cancellation, a separate persistent run-wide Stop,
failure recovery, and renderer capability limits.
The selector cannot retarget an existing run, and a broad-scope proposal still
shows and authorizes its exact narrowed panel action. Run-local YOLO remains
deliberately exact-panel-only. A provider adapter still never receives a
broker, session-host client, terminal object, or executor.

## Consequences

- Desktop packaging and lifecycle remain within the existing .NET process.
- Cancellation, telemetry correlation, and session-host calls do not cross an
  extra IPC boundary.
- Provider adapters can be added independently while policy and tools remain
  provider-neutral.
- Asura owns the correctness of streaming assembly, tool-call sequencing,
  steering, and compaction and must test those behaviors directly.
- Provider configuration changes invalidate a live run instead of silently
  changing endpoint, model, or credential scope under an existing transcript.
- The governed runtime can be replaced by a future headless presentation
  without moving provider or execution authority into the agent kernel.
- Moving the loop out of process later remains possible behind the application
  contracts, but isolation is not treated as authorization.

## Alternatives rejected

- A Pi/Node.js child process adds a runtime and protocol solely for the agent
  without removing any Asura security responsibility.
- Letting provider adapters call terminal, browser, file, or MCP operations
  directly creates a hidden control plane.
- Binding Core, Application, or Protocol to one provider SDK makes provider
  fallback and local-model support expensive.
- Treating same-user local processes as trusted would create an undocumented
  desktop control surface and complicate future headless authentication.
