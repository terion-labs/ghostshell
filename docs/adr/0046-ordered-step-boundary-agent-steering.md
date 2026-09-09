# ADR 0046: Ordered step-boundary agent steering

- Status: Accepted
- Date: 2026-08-17
- Supersedes the desktop presentation and scheduling decisions in
  [ADR 0037](0037-bounded-native-provider-steering.md)
- Extends:
  [ADR 0017](0017-native-dotnet-agent-runtime.md)
- Reference behavior: `references/pi/packages/agent`

## Context

A user must be able to add direction while an agent is working without losing
the current response, interrupting a partially executed tool batch, or waiting
for an entire multi-step run to finish. Replacing the active provider
generation is too narrow: it is unavailable during tools and approval states,
changes the composer into a Stop-or-Steer control, and discards useful work
that is already reaching a stable boundary.

Pi separates ordinary follow-ups from steering. Both are ordered user input;
steering is selected before the next model step, after the current model step
and any resulting tool batch have settled. Asura needs the same scheduling
model while preserving its stricter authorization and tool-result boundaries.

## Decision

Each active workspace run owns one bounded, ordered queue of human-authored
messages. A queued item has a stable opaque identifier, copied message text,
reasoning effort, and one of two delivery classes:

- **Follow-up** runs when the agent would otherwise finish the current run.
- **Steering** runs at the next safe step boundary, ahead of ordinary
  follow-ups.

Steering items preserve submission order. Explicitly choosing **Steer** on an
ordinary queued item promotes that item to the front of the steering partition.
Users can edit, delete, and reorder items without crossing the steering and
ordinary partitions. The runtime limits a turn to eight accepted queued items
and 256 KiB of aggregate UTF-8 message data.

### Safe boundary

Steering never cancels the provider operation already in progress and never
skips part of a proposed tool batch. The runtime consumes the next steering
item only after one of these boundaries:

- the current provider response has completed; or
- every proposal in the current correlated tool batch has produced its ordered
  result, those results have committed to the kernel transcript, and required
  maintenance and live-tool refresh have completed.

At that boundary the kernel appends a real user message to the retained
conversation and starts the next provider request. It does not rewrite the
previous user message. Incompatible provider-private replay state remains
subject to its exact provider/protocol/model binding.

An uncertain external mutation still follows the existing quarantine rule;
queued text cannot turn an unknown outcome into a known one. Rejected or
uncommitted queued input is returned for draft recovery on failure. Stop and
Clear discard the active run's remaining queue.

### Presentation

The composer remains enabled while the provider is streaming, a tool is
running, or a dedicated approval, question, or capability decision is pending.
The primary action remains the send arrow and Stop remains a separate header
action.

- **Enter** appends an ordinary queued follow-up while busy.
- **Shift+Enter** inserts a newline.
- **Command/Super+Enter** appends steering while busy and sends normally while
  idle.
- A queued row exposes **Steer**, edit, delete, move-earlier, and move-later
  operations.

The queue belongs to the workspace runtime and is therefore isolated from
other application and Quick Terminal workspaces.

### Authority

Queued input conveys no approval, capability decision, tool permit, target
change, or full-access authority. It is never interpreted as a response to a
dedicated decision card. Every later tool proposal still passes through the
normal catalog, policy, approval, broker, SessionHost, completion-audit, and
quarantine path.

The earlier generation-replacement primitive remains an internal fenced kernel
capability, but desktop presentation does not route user input to it.

## Consequences

- Users can continue typing during long runs and decide which queued message
  should influence the next agent step.
- Current provider work and complete tool batches settle deterministically
  before steering is applied.
- Visible conversation history retains steering as a distinct user turn.
- Queue mutation is bounded, identity-based, workspace-scoped, and independent
  from Stop.
