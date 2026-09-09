# ADR 0037: Bounded native provider-generation steering

- Status: Superseded for desktop presentation and scheduling by
  [ADR 0046](0046-ordered-step-boundary-agent-steering.md)
- Date: 2026-07-24
- Extends:
  [ADR 0017](0017-native-dotnet-agent-runtime.md) and
  [ADR 0018](0018-native-ai-provider-and-chat-boundary.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

> Historical decision: the bounded generation-replacement primitive remains a
> fenced kernel capability, but the desktop no longer presents or schedules
> steering this way. The current product contract is ADR 0046.

A provider can begin a long answer in the wrong direction while the user still
has better task intent to supply. Requiring Stop followed by a new prompt loses
the in-flight turn, while appending an ordinary second user turn would let the
first answer commit before the correction. Pi demonstrates useful steering
behavior, but it remains a reference only; adding a Node.js process for this
interaction would contradict Asura's native in-process agent boundary.

Steering is also easy to confuse with authority. Text such as “approve it”,
“enable the tool”, or “use another host” must not answer a pending
clarification, decide a capability request, approve an action, change policy,
or retarget a run. Replacing a provider generation must additionally fence
late output from an adapter that ignores cancellation.

## Decision

Asura adds one typed application operation:

- `agent.steer`.

The first slice accepts exactly one human-authored update while the initial
provider generation of one top-level user turn is actively streaming. It is
unavailable during target resolution, tool-result provider continuation,
clarification, capability decision, action approval, tool execution,
cancellation, failure, or a completed turn. It is not a model tool and cannot
be invoked by provider output.

### Governed application boundary

`GovernedAgentSteering` contains only the exact current `AgentRunId`, the
expected positive kernel generation projected with steering availability, and
one nonblank copied update of at most 64 Ki characters. Both identities are
required: one run can contain several top-level turns, so a delayed command
prepared for an earlier turn must not steer a later generation in that same
run. The presentation receives `SteeringAvailable` and its generation only
after the run has pinned its exact target, provider, policy, tool manifest,
native session, turn-cancellation owner, and initial kernel generation.

Before applying the update, the governed runtime re-inspects the pinned target
identity and the current live topology for Workspace or internal `OpenTab`
targets. Under its lifecycle gate it then revalidates:

- the exact run, target, native session, and turn-cancellation owner;
- the immutable provider binding, provider revision, and current-profile
  status;
- baseline, run, and effective policy values plus policy generation;
- the absence of a question, capability request, approval, or active tool.

Drift fails closed without changing the visible user input. Steering does not
call the capability broker, request or consume a permit, invoke SessionHost,
dispatch a panel action, or create an action-audit or policy-transition row.
It can influence only the provider's next inert response and tool proposals;
every later proposal still follows its ordinary trusted classification,
policy, exact approval, one-action authorization, execution, and audit path.

### Kernel linearization and provider fencing

`NativeAgentSession.Steer(expectedGeneration, update)` is the transcript
linearization point. Under the kernel gate it accepts only the current initial
user generation, rejects stale or repeated calls, rechecks the base
conversation, validates the combined UTF-8/conversation bounds, and reserves
capacity for one replacement provider operation before returning success.

Commit, Cancel, and Steer therefore have one consistent winner:

- if the original commit wins, steering reports that the generation is no
  longer available and does not rewrite committed history;
- if steering wins, the old generation is cancelled and generation-fenced,
  and the original `RunTurnAsync` owner transparently runs the replacement;
- if Stop or caller cancellation wins afterward, the replacement is cancelled
  and neither generation commits.

The replacement preserves the original provider instance, exact tool
definitions, base transcript, run, target, and policy. It commits one revised
user message:

```text
<original user input>

Steering update:
<human update>
```

It does not add a second committed user turn. At most one steering replacement
is permitted for that top-level turn. A tool-result continuation is never
steerable.

Provider cancellation is advisory, so acceptance reserves the second bounded
provider-operation slot before the old stream is detached. Old and replacement
operations both retain their own cancellation lifetime and release their own
slot. The request-scoped `IAgentProvider` contract therefore requires at most
two independent concurrent stream enumerations on the same instance; built-in
Anthropic and OpenAI-compatible adapters have same-instance overlap
conformance coverage. Every provisional delta and tool call is accepted only
from the current generation. A secret-free `TurnSteered` event identifies the
superseded generation but contains no prompt text or update.

The governed watcher tracks the replacement generation atomically, clears old
provisional text, and filters already-queued old-generation deltas. The
visible transcript is revised only after kernel acceptance. The final kernel
transcript remains the source of truth when the provider operation completes.

### Presentation and lifetime

While the initial response is steerable, the existing agent composer remains
keyboard reachable, changes its placeholder and primary action to **Steer**,
and keeps the independent **Stop** action visible. An empty draft cannot be
submitted, an in-flight attempt cannot double-submit, and a rejected or
cancelled attempt restores the draft when the user has not already typed a
replacement.

The same composer is disabled for questions, capability decisions, approvals,
tool execution, and provider continuations. It never interprets steering text
as one of those dedicated decisions and does not resolve a fresh workspace
target. The ordinary prompt privacy boundary applies: steering content may be
sent to the selected provider and retained only in the bounded in-memory run
conversation, but it is excluded from action audit, policy audit, recovery,
diagnostics, and normal logs.

Stop, Clear, run failure, run completion, disposal, provider invalidation, or
policy/target drift removes steering availability. Headless mode, ACP/A2A, and
external steering identity remain out of scope; a future transport must add an
authenticated human-input contract rather than exposing this desktop method
as ambient IPC.

## Consequences

- A user can correct one active native provider response without a Node/Pi
  sidecar or software on the operated machine.
- A non-cooperative old provider may finish late, but its output and tool calls
  cannot enter the transcript or acquire application authority.
- Steering preserves the exact run manifest and one-turn transcript shape,
  while Stop and normal approval controls remain independent.
- The deliberately one-update, initial-generation-only slice avoids a queue,
  follow-up scheduler, or implicit multi-turn rewriting protocol.

## Alternatives rejected

- Starting a second ordinary prompt would allow the old answer to commit and
  would create different transcript semantics.
- Mutating a committed turn would make replay and audit reasoning
  nondeterministic.
- Reusing clarification, capability, or approval APIs would blur task intent
  with trusted decisions.
- Cancelling the old adapter without a reserved replacement slot would exceed
  the bounded provider-work limit when cancellation is ignored.
- Launching Pi in Node.js would add a runtime and process boundary without
  supplying any missing authorization control.
