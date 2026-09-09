# ADR 0019: One-action agent capability broker

- Status: Accepted
- Date: 2026-07-23
- Extends:
  [ADR 0017](0017-native-dotnet-agent-runtime.md),
  [ADR 0018](0018-native-ai-provider-and-chat-boundary.md)
- Terminal-engine update:
  [ADR 0040](0040-cross-platform-libghostty-vt-terminal.md) supersedes the
  platform-split renderer/shim dispatch details in this record. The capability,
  authorization, lease, commit, and audit decisions remain accepted.
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

The native agent loop can assemble provider tool proposals, but those proposals
are untrusted data. Target context, a model-supplied tool name, a UI permission
mode, or an old approval cannot by itself authorize a terminal or application
operation.

Desktop v1 needs a provider-neutral authorization boundary that remains useful
for future headless transports. It must not depend on Avalonia dialogs, provider
payloads, terminal engines, or generic string-based execution.

## Decision

Asura uses a closed application-owned tool catalog. Trusted descriptors
assign each tool a capability and risk; model/provider risk labels are ignored.
The catalog covers exact workspace/tab/panel inspection, bounded terminal
screen/wait/input/interrupt/resize operations, browser state/navigation and
candidate exact-object interactions, and bounded read-only File Viewer
list/stat/text-preview observations. Adding a tool is a code and test change,
not runtime schema expansion from provider content.
All five workspace-graph tools—`workspace.inspect`,
`tab.list`, `panel.list`, `panel.inspect`, and `panel.focus`—are
production-reachable through this governed path.
Workspace layout mutations—`tab.create`, `tab.close`, `panel.add`,
`panel.split`, and `panel.close`—use the same one-action boundary under the
separate `WorkspaceLayout` capability. SessionHost verifies the exact complete
graph before consuming authority and verifies a fresh committed graph after the
trusted desktop layout port returns.

Capabilities distinguish terminal reads, terminal input, destructive terminal
actions, file reads/writes, Git mutation, browser navigation/data, network
fetch, Docker, processes, MCP, and secret use. Policy modes are `Off`, `Ask`,
`Auto`, and `Yolo` (displayed as `Full access`). Default terminal input is `Ask`.
`Auto` authorizes trusted observation/routine classifications only; mutation,
destructive, and privileged classifications require approval. `YOLO` may
authorize those classifications but never bypasses target binding, run
lifecycle, policy generation, cancellation, secret isolation, or audit.

Effective policy resolves from global, workspace, screen, and run layers. The
most specific explicit capability value wins. The broker then owns the
effective run policy, exact target scope, policy generation, agent identity,
and approving desktop-client identity for the life of the run. Proposal and
execution callers cannot supply replacement policy. Older durable policies
remain readable; newly introduced capabilities absent from an old policy fail
closed or inherit a more general explicit layer. Malformed layers are rejected
rather than partially applied.

`YOLO` is not authorized by the enum value alone. Selecting Full access creates
a human confirmation bound to the run, exact target identity, approving desktop
client, and policy generation. Selecting Ask, stopping, clearing, or replacing
the run revokes pending approvals and issued tokens and cancels active permits
before waiting for broker audit I/O.

An `AgentActionProposal` binds:

- authenticated agent actor and run;
- closed tool name;
- immutable requested target;
- a digest of the exact resolved target/session revision set;
- a digest of canonical material arguments;
- a bounded, non-persisted approval presentation;
- policy generation, creation time, and deadline.

Only trusted application composers can create an executable proposal. The
terminal, browser, File Viewer, and panel composers accept closed typed request
unions, select one exact compatible session from the resolved scope, narrow a
broader scope to that panel, and derive both the canonical argument digest and
complete approval presentation from the same ordered fields. Their canonical
encodings are versioned, length-prefixed, and culture invariant. Panel action
digests contain only the closed tool and trusted panel identity; request
material in the other families additionally rejects unrepresentable,
oversized, or likely literal-secret values as applicable. Prepared actions,
executable proposals, and execution bindings have no public constructors.

The broker durably claims `requested` before making authority available. `Off`
records `denied`. `Ask` creates a bounded approval request for the run's bound
desktop client; desktop v1 initially supports only a one-action duration.
`Auto`, a confirmed `YOLO`, or a matching human approval can issue a random
opaque authorization bound to the exact proposal. The token expires quickly
and is atomically consumed once. Durable action IDs prevent a process restart
from minting a second authorization for the same action.

Immediately before a typed operation starts, the session host re-resolves the
exact target and live session under the host graph gate, recomputes an
execution binding from the same prepared request, captures the exact terminal,
browser, or File Viewer typed port where applicable, and consumes the
authorization itself. No permit can be paired with
out-of-band arguments. Mismatch, replay, expiry, revocation, policy change,
lease/attachment loss, missing audit storage, or cancellation fails closed.
The broker records `started` before returning a permit. The host links caller,
run/policy, session-lifecycle, attachment, and input-lease cancellation,
dispatches only through the captured typed port or guarded workspace graph, and
attempts to record exactly one `succeeded`, `failed`, or `cancelled` outcome
without retrying the tool operation. If the completion audit cannot be
confirmed, the broker removes
the action from the active set but retains the exact immutable completion and
deterministic audit event in a bounded quarantine. It suspends the run, rotates
the run's same-generation authority signal, cancels the old signal, and rejects
new or already-issued authority until the exact event is reconciled. The host
may retry only that same completion; changed retries fail closed, and no
completion retry redispatches the operation. If the bounded host retry remains
unresolved, the runtime cancels the run and stops provider continuation with
`agent_completion_audit_unavailable`.

Above the host permit boundary, the governed runtime gives each authorized
dispatch its own linked, identity-tracked cancellation source. The user may
request cancellation once from the exact visible active-tool card. The host
records the cancelled tool outcome and the provider receives a structured
`caller_cancelled` result, allowing the run to continue without reusing the
one-action authorization. Duplicate or post-completion requests are typed
no-ops. Run-wide Stop remains a separate operation: if it races action
cancellation, whole-turn revocation wins and no provider continuation occurs.
The host normalizes cancelled waits and cancellation exceptions to a failed
result bearing the exact audited cause. Permit, runtime, and input-scope
revocation outrank caller cancellation, so provider results cannot contradict
the durable audit.

Agent-action phase IDs and durable state transitions are deterministic and
idempotent. SQLite atomically enforces
`requested -> approved/denied -> started -> succeeded/failed/cancelled`.
Startup reconciles an incomplete `started` action to `failed` with the
stable `application_restart_outcome_unknown` failure before provider or UI
work begins. Recovery cannot prove whether an external side effect committed,
so it must not label an incomplete started action as cancelled.

Audit details are a closed, explicitly encoded value shape. They contain the
run, trusted capability/risk, permission/decision, policy generation,
target-identity digest, opaque approval/authorization ID digests, approval
duration, authority expiry, execution duration, optional bounded counts or
artifact references, authorization source, and stable result code. They do not
contain prompts, screen/command output, raw arguments, approval display values,
or secrets.

Run-local capability escalation has a separate broker-owned audit chain. The
broker persists a closed, secret-free `Requested` event before UI presentation
and exactly one terminal decision. The decision is one of allowed, denied,
expired, cancelled, target/capability/policy drift, or audit failure. An allowed
Off-to-Ask policy transition carries the same request ID in its closed policy
details. Audit ambiguity suspends the run and grants no authority; ordinary
per-action approval remains a separate later step.

The desktop composition registers the broker, its SQLite audit dependency, the
session-host executor, a composition-owned human approval principal, and the
provider-neutral `IGovernedAgentRuntime`. The runtime supplies only its closed,
capability-filtered tool definitions. It turns a model proposal into a trusted
typed request and returns a bounded structured result only after the host
consumes the exact one-action authorization. Provider adapters receive neither
the broker nor an executor.

The first production surface is deliberately narrower than the complete M3
catalog: its visible run scope is the current `Workspace`, and it exposes the
closed contributed panel, terminal, browser, File Viewer, Process Monitor,
Statistics, MCP, and intrinsic tools supported by that workspace. The desktop
pins the exact window/workspace identity and accepts no provider-supplied scope
identity. Before the initial provider call and after every tool-result round,
the runtime re-inspects that workspace, accepts its current ordered eligible
panel topology, and rebuilds the provider tool schemas and context projection.
Newly opened eligible panels can therefore appear and closed panels can
disappear without retargeting the run.

`AgentTarget` still retains exact panel/session, `OpenTab`, and explicit
selected-terminal variants as internal/testable contracts. Exact and selected
targets pin their complete membership and fail closed on disappearance,
replacement, or structural drift. `OpenTab` follows the same live-topology
refresh rule as Workspace while retaining its exact window/workspace/tab
identity. These variants are not additional visible desktop scope choices.

For every broad-scope proposal, the current schema requires a host-enumerated
eligible `panel_id`. The runtime parses it against a fresh resolution and the
trusted composer narrows the request to that exact panel/session before
approval. SessionHost revalidates that binding adjacent to one-action permit
consumption and dispatch, so topology may change between provider rounds but
cannot silently retarget an action already being authorized or executed. The
provider receives bounded host-generated panel IDs and descriptive title, tab,
connection, and working-directory metadata plus supported operations. Those
labels remain untrusted content.

The two production panel tools have no generic graph-command or provider
execution path. For an exact panel/session target their schemas are closed empty
objects. For any broader target they require exactly one `panel_id`, whose enum
contains only current active graph-backed panels, even when the enum has one
value. The parser rejects unknown fields and out-of-scope IDs; the trusted
composer narrows the request to that exact graph panel. `panel.inspect` is a
`Search` observation and returns fresh host-owned identity, revision,
lifecycle, health, visibility, focus, and activity state. Descriptive titles,
connection boundary, and working directory are individually bounded and
redacted and the envelope is marked
`content_origin=untrusted_panel_metadata`. `panel.focus` is a `RunCommands`
routine action. The host holds its graph gate while it resolves and binds the
exact panel, consumes the one-action permit, re-resolves and compares the
binding, and performs the adjacent cancellation check. Only then may it call
the expected-revision synchronous graph activation, which is the commit point.
Revision/session drift or permit mismatch therefore cannot focus a panel.
Cancellation after commit cannot erase the receipt, and an already-focused
panel returns `changed=false` without advancing graph revision or sequence. The
provider receives only the committed window/workspace/tab/panel identity,
revision, sequence, and change flag.

An exact single-terminal tool schema omits `panel_id`. Every broader Workspace
schema and every internal `OpenTab` or selected-terminal schema requires
`panel_id` and enumerates only panels that support that tool's capability, even
when one terminal is currently eligible. The parser checks the selected ID against a fresh target
resolution immediately before composition. The trusted composer then narrows
the enclosing scope to one exact panel and session; approval presents that
exact action target, and the structured result includes the trusted panel ID.
Tools that inject terminal input are advertised only when the selected live
renderer can uphold the human-preemption invariant.
The host exposes that proof as the explicit `terminal.agent_input_barrier`
capability rather than inferring it from a renderer name. Both the managed
Avalonia presenter and shared libghostty-vt engine participate in the barrier
on every desktop; a session without it receives no agent input tools. Resize is
governed independently by exact interactive-attachment authority and the
serialized resize transaction.

Send-mouse is one closed terminal-cell event, not generic cursor or desktop
automation. Its schema permits only a fixed button/event vocabulary, bounded
zero-based column and row, and unique known modifiers. The trusted composer
binds every one of those fields plus the exact session into the approval and
argument digest. The host independently requires both `terminal.mouse` and
`terminal.agent_input_barrier`, consumes the exact authorization, and acquires a
one-action input lease before typed dispatch. Human input preempts the lease;
success returns only a receipt and terminal-outcome uncertainty follows the
same quarantine path as other input mutations.

Paste is a distinct governed mutation, not an alias for send-text and not an
ambient clipboard read. Its provider schema accepts one non-empty, well-formed
Unicode value of at most 2,048 UTF-8 bytes; tab, carriage return, and line feed
are the only permitted control characters. The trusted composer revalidates
that shape, rejects likely literal-secret material, binds the exact raw text to
the argument digest, and renders controls and formatting characters with
reversible escaping for approval. The tool is advertised only when the live
terminal exposes both `terminal.paste` and
`terminal.agent_input_barrier`. The catalog marks it as a mutation, so ordinary
`Auto` cannot authorize it; the host additionally accepts only an exact
`HumanApproval` or already-confirmed run-local `YoloPolicy` source. After
rechecking both capabilities, the host acquires one one-action input lease and
dispatches to the typed paste port only at that final trusted boundary. Paste
input carries text, not a caller-supplied approval flag; the one-use capability
authorization is the authority. The shared terminal engine keeps the caller/lease token on each queued
mutation through the PTY write, which is its irreversible commit point. A
normal receipt remains gated by flush, but post-commit cancellation or flush
failure completes that committed receipt before failing the session; this
prevents an already-written command from being presented as safely retryable.
Queued cancellation, writer failure, and shutdown still settle every
uncommitted acknowledgement. The shared path performs the same adjacent
human-authority check on every desktop, and engine tests cover both
current-authority guarded paste and stale-authority rejection after physical
input advances authority. A successful paste returns only a receipt. Engine
confirmation refusal, invalid results, audit uncertainty, or cancellation
never causes a second paste dispatch.

Resize is a closed mutation rather than a provider-selected renderer action.
Its schema contains exact integer `columns` from 2 to 1,000 and `rows` from 1
to 1,000, plus the existing enumerated `panel_id` in a broad scope. The runtime advertises
it only when the terminal reports `terminal.resize` and a fresh host snapshot
contains exactly one interactive attachment owned by the authenticated visible
desktop client. The runtime preserves that attachment's trusted logical
dimensions and render scale; the provider cannot supply or observe the
attachment identity. The composer binds the exact attachment and every
viewport field into the approval and argument digest. The host captures the
same attachment authority and exact session revision immediately before
dispatch, then serializes renderer, human, and governed resizes through one
per-session gate that covers both the terminal-engine call and attachment
metadata commit. An unrelated revision or late caller cancellation after a
successful engine return cannot split those states; changed attachment
authority still fails closed. libghostty-vt state and Porta.Pty apply and
verify the exact cell grid. Absence, ambiguity, replacement, revocation, or
exact-grid failure never causes inference, substitution, or a second resize.

The desktop renders the current ordered Workspace topology in an expandable
context inspector. Each published snapshot is immutable, but Workspace and
internal `OpenTab` snapshots are replaced after a successful round refresh;
exact and selected-target snapshots retain their pinned membership. Rows expose
exact identities, state, and advertised operations alongside bounded/redacted
untrusted labels. This projection is descriptive evidence only: it carries no
attachment, permit, authorization, or execution path, and it disappears when
the run is cleared.

Run-local YOLO is an ephemeral runtime overlay, not a durable setting. The
runtime rejects any baseline policy containing YOLO. Enabling it requires an
explicit selection and a confirmation from the composition-owned authenticated
desktop principal. It overlays every capability in that live run.
The same contract supports the visible Workspace scope and the internal/testable
exact-panel, `OpenTab`, and selected-panel targets. The desktop keeps that mode
selected until the user chooses Ask or the run ends. Disable advances the
broker-owned policy generation even while a tool is active, cancels the old
generation and permit, and restores the baseline per-action policy before
another action can start.

Each policy change stays suspended until a deterministic, secret-free
transition record is durable. That record binds the run, generation, exact
target-identity digest, transition (`enabled`, `disabled`, `expired`, or other
update), and optional legacy bounded-authority expiry. A retry after ambiguous storage completion
accepts only the exact previously committed event. The live agent surface
shows the selected approval mode directly in the composer.

## Consequences

- Provider adapters and the native loop remain unable to execute application
  operations directly.
- Approval replay and changed target/argument use fail because authorizations
  are exact, expiring, generation-bound, and single-use.
- Audit availability is part of authorization availability.
- Trusted risk classification and policy evaluation are testable without UI or
  terminal engines.
- A descriptive agent context snapshot is not reusable authority.
- The typed terminal composer, execution-time fingerprint recomputation,
  session-host consume-and-execute bridge, active cancellation, human input
  preemption, and restart-safe action audit state are implemented and covered
  by end-to-end broker/host tests.
- All five workspace-graph tools are implemented through closed typed composers
  and session-host ports. Exact/broad schema selection, scope-clipped reads,
  permit-before-focus, revision drift, committed receipts, already-focused
  revision stability, and bounded redacted results have focused automated
  coverage.
- Governed provider tool composition, native structured result continuation,
  the visible Workspace approval/run surface, exact active-tool cancellation,
  and a separate persistent run-wide Stop control are implemented. Workspace
  topology refreshes between rounds; each action is selected with a
  capability-specific panel ID and narrowed to exact panel/session approval and
  execution. Exact, `OpenTab`, and selected-terminal variants remain internal
  contracts.
- The host/working-directory and run-local `YOLO` lifecycle, including broad
  Workspace scopes containing terminals, immediate in-flight disable,
  run teardown, and policy-transition audit, is covered as a confirmed
  run-scoped contract. Browser and MCP actions consume the same confirmed
  run-local authority while retaining their typed host and session checks.
- Completion-audit uncertainty quarantines the immutable result, revokes run
  authority, prevents provider continuation, and never retries terminal input.
- Saved-screen-template targeting is now a separate two-phase UI workflow:
  template selection is non-authoritative, the normal activation path creates
  a revisioned live tab, and explicit review accepts only its ordinary
  `OpenTab` identity. Additional persistent approvals remain separate work.
  Broad scope does not create persistent or cross-run authority.

## Alternatives rejected

- Passing an `IServiceProvider`, session-host client, or terminal object to the
  agent loop creates ambient authority.
- Trusting provider tool schemas or risk labels lets prompt injection redefine
  permissions.
- Long-lived bearer permissions make replay and scope drift difficult to
  contain.
- Recording only successful actions omits denied, cancelled, and partially
  started security evidence.
- Treating context graph revision or a disabled button as authorization leaves
  execution vulnerable to stale state and non-UI callers.
