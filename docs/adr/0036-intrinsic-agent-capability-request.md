# ADR 0036: Intrinsic run-local capability request

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0017](0017-native-dotnet-agent-runtime.md) and
  [ADR 0019](0019-one-action-agent-capability-broker.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

Asura deliberately keeps some agent capabilities `Off`. A live run can
still advertise ordinary production tool definitions for such a capability
when the target supports them; an attempted action remains inert because the
broker denies the disabled capability. Ending the turn and asking the user to
edit durable policy loses the exact provider continuation. Letting the model
edit policy, choose a permission, or treat a capability change as an action
approval would instead create a self-escalation path.

The useful bounded operation is narrower: the model may ask whether one
currently unavailable capability should become `Ask` for this live run. The
human decision must be separately authenticated and conspicuous, must approve
no action, and must leave every later terminal, file, browser, process, and
other operation subject to its ordinary exact authorization.

## Decision

Asura adds one intrinsic runtime tool:

- `agent.request_capability`.

It is implemented in-process by the native .NET runtime. It is absent from
`AgentToolCatalog`, has no `AgentActionRisk`, and is not an application action.
The request itself never calls the broker's ordinary action-request path,
receives or consumes a one-action permit, invokes SessionHost, or dispatches an
operation to a terminal, browser, file provider, process monitor, or other
target.

### Dynamic advertisement and closed request

The intrinsic is advertised only after the runtime has resolved a valid pinned
target and composed the final ordinary production tool set for that provider
request. Its candidate set contains only capabilities which:

- are mapped by the trusted catalog from at least one ordinary cataloged tool
  that is actually present in that final tool set; and
- are `Off` in the current run policy.

The intrinsic is omitted when that set is empty or a YOLO overlay is active.
It does not advertise capabilities merely because they exist in an enum,
policy, session capability list, or target manifest.

The input schema is the closed object:

```json
{
  "capability": "process_control"
}
```

`capability` is required and its dynamic enum contains only the candidate
capabilities as explicit stable lower-snake-case protocol tokens. The mapping
does not depend on CLR enum names. A call contains exactly one capability;
duplicate, unknown, stale, non-string, or extra fields fail closed. There is
no model-supplied reason, prose, target, tool name, permission, duration,
persistence choice, approval identifier, or UI instruction.

### Separate authenticated decision

An accepted call creates a fresh opaque `AgentCapabilityRequestId`, a
two-minute UTC expiry, and the dedicated
`AwaitingCapabilityDecision` runtime state. The pending presentation contract
binds the exact run, capability and stable token, policy generation, pinned
target, trusted target title, and trusted titles of the affected ordinary
tools. Model text is never copied into this contract.

The desktop renders a distinct authenticated capability card with only trusted
application-owned content. Its explicit choices are **Enable Ask for this
run** and **Keep Off**. The card warns that enabling the capability approves no
action and that every later operation still needs its normal authorization.
It does not reuse the clarification card, `AgentQuestionId`, ordinary action
approval card, `AgentApprovalId`, or their decision APIs.

At most one accepted capability request may reach a human decision during one
top-level `SendAsync` turn, and each request names only one capability. A
provider cannot obtain several grants by emitting parallel calls or repeated
tool rounds in the same turn.

Under the runtime gate, a decision verifies the exact random ID, run, expiry,
policy generation, pinned target, currently advertised ordinary tools, and
that the requested capability remains an eligible `Off` candidate. The
complete target and candidate set are re-resolved before presentation and
again before applying an allowed decision. Target, membership, advertised-tool,
policy, YOLO, cancellation, or lifecycle drift fails closed.

Decision submission checks caller cancellation immediately before atomically
claiming the pending request. Once claimed, later caller cancellation cannot
reopen it or make retry safe. Stop, whole-turn cancellation, disposal, expiry,
or starting a new run clears the request and completes its waiter without
binding a late decision to another run.

### Exact run-local policy transition

The runtime distinguishes three policy values:

- **baseline policy** is the immutable trusted durable/captured policy and
  provider/model provenance accepted for the run;
- **run policy** starts as the baseline and contains only bounded live-run
  grants made by this mechanism;
- **effective policy** is the policy registered with and enforced by the
  broker, including any separately confirmed YOLO overlay.

Allowing a capability request changes exactly one run-policy permission from
`Off` to `Ask`. It cannot grant `Auto` or `YOLO`, change provider or model,
modify another capability, widen the target, or persist to a screen,
workspace, recovery record, or global configuration. A confirmed YOLO window
overlays the run policy rather than replacing it; disabling or expiring YOLO
restores the run policy, including any prior bounded `Ask` grant. Capability
requests are unavailable while YOLO is active.

The allowed transition uses the broker's authenticated run-policy update
operation. The broker revokes the previous authority generation and commits
the deterministic policy-transition audit before the intrinsic may report
success. An unavailable or rejected transition cannot expose the grant. An
ambiguous or failed broker/audit transition follows the existing fail-closed
policy path: current authority is revoked and the run remains
suspended/quarantined. This path never calls the broker's ordinary
`RequestAsync` action-authorization operation and produces no reusable permit.

Keeping `Off` and expiry make no policy change and create no ordinary action
audit. The successful provider result is bounded to:

```json
{
  "ok": true,
  "capability": "process_control",
  "permission": "ask",
  "scope": "run",
  "action_approval_required": true
}
```

It omits request and approval identifiers, target data, affected-tool titles,
model prose, actor identity, and audit details. Denial, expiry, stale state,
target drift, and policy drift return fixed non-echoing failures. After a
successful transition, an ordinary tool call still goes through its trusted
catalog classification, exact proposal composition, broker decision, separate
action approval when required, one-action authorization, SessionHost
revalidation, dispatch, and action audit.

### Lifetime, persistence, and future transports

Pending requests and run-policy grants exist only in the live in-memory run.
Stop, clear, disposal, and a new run discard both. They are not restored from
recovery or written into durable policy definitions. Only an allowed
transition enters the existing deterministic run-policy audit; request model
data, denials, and expiry do not enter the action audit.

Headless mode, ACP, A2A, and external approval routing remain out of scope.
Those transports require separate caller identity and decision-routing
contracts and must not infer a grant merely because no desktop card is
available.

## Consequences

- A provider can request one narrow, human-mediated `Off`-to-`Ask` change
  without a Node sidecar or software on the operated machine.
- The user can make one disabled production capability usable for the current
  run without changing saved configuration.
- Enabling a capability never authorizes the action that motivated the
  request; the next ordinary call still pauses for its own exact approval
  under `Ask`.
- Dynamic advertisement, two-phase revalidation, a generation-bound update,
  and one-way decision claim prevent stale or parallel requests from widening
  authority.

## Alternatives rejected

- Letting the model select `Ask`, `Auto`, `YOLO`, duration, or persistence
  would turn a narrow request into policy editing.
- Reusing ordinary action approval would conflate permission to expose a tool
  with permission to execute one exact action.
- Persisting the grant would let untrusted provider behavior alter future
  runs, workspaces, or screens.
- Advertising every `AgentCapability` would reveal or request authority that
  no actual production tool in the current target can use.
- Launching a Node/Pi subprocess would violate the native in-process runtime
  boundary without adding an authorization control.
