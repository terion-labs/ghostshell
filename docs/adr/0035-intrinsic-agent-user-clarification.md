# ADR 0035: Intrinsic agent-to-user clarification

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0017](0017-native-dotnet-agent-runtime.md) and
  [ADR 0033](0033-intrinsic-agent-progress-reporting.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

A terminal-operating agent sometimes lacks one piece of user intent that
cannot be discovered safely from a terminal, browser, file panel, workspace
graph, or local Process Monitor. Ending the turn and asking the user to start a
new prompt loses the exact pending provider tool continuation. Treating a
clarification as an approval is worse: a model-generated question and a
free-text answer must never create capability authority, widen scope, or
authorize a command.

The clarification text is also untrusted model output. It can attempt to
phish a credential, impersonate Asura's trusted approval UI, flood the
surface, or bind a late answer to a different question. A user's answer can
itself contain unsafe Unicode or a literal secret. The first slice therefore
needs a small native contract with an explicit non-authority boundary.

## Decision

Asura adds one intrinsic runtime tool:

- `agent.ask_user`.

It is advertised after a valid run target has been resolved, alongside
`agent.report_progress`. It is not present in `AgentToolCatalog`, maps to no
`AgentCapability` or `AgentActionRisk`, requests no broker authorization,
dispatches no SessionHost or panel operation, and creates no action-audit
chain. It is a native in-process continuation mechanism, not an alternate
approval path.

### Closed model request

The input schema is the closed object:

```json
{
  "question": "one concise non-sensitive question"
}
```

`question` is required, single-line, strict Unicode, nonblank, free of control,
format, line-separator, and paragraph-separator code points, free of
literal-secret-shaped material, and at most 1,024 UTF-8 bytes. Duplicate and
unknown fields fail closed. The first slice has no model-supplied choices,
target, timeout, default answer, secret flag, approval ID, permission, or UI
instructions.

The tool description and system prompt restrict it to missing non-sensitive
task intent. It must not request credentials, tokens, keys, approval,
permission, capability changes, or confirmation for another tool. These
instructions are defense in depth; trusted runtime and broker checks still
decide every later action.

### Pending-question lifecycle

Each accepted request receives a fresh opaque `AgentQuestionId` and a two-minute
UTC expiry. The provider/tool workflow itself has no whole-turn deadline. Runtime state
transitions from `StreamingProvider` to `AwaitingUserInput`. One pending
question is exposed in the presentation snapshot; current progress, approval,
tool activity, and provisional text are cleared atomically. Stop remains
available, normal prompt sending and action cancellation do not.

The runtime resolves and checks the complete pinned target immediately before
publishing the question. A response can be either:

- `Submitted`, containing one single-line strict-Unicode, nonblank,
  literal-secret-free answer of at most 2,048 UTF-8 bytes; or
- `Declined`, containing no text.

Under the runtime gate, response submission checks the exact current random ID,
checks `now >= ExpiresAtUtc`, rejects a duplicate response, and atomically
claims the response. Caller cancellation is checked immediately before that
claim. Once claimed, late caller cancellation cannot reopen the question or
make a retry safe.

The whole-turn token then re-resolves the complete pinned target before the
answer is placed in a tool result. Target drift discards the answer and returns
only `target_changed`. Stop, expiry, disposal, or turn cancellation clears the
visible question and completes every waiter, so a UI response cannot hang or
attach to a later question.

### Provider continuation and visible history

A submitted answer produces bounded JSON containing only:

- `ok=true`;
- `content_origin=user_supplied_agent_answer`;
- `answer`.

The question and correlation ID are not echoed. Decline and expiry produce
fixed non-echoing failures, `user_input_declined` and `user_input_expired`.
The answer is intent data, never authorization.

The existing `NativeAgentSession.SubmitToolResultsAsync` lock is the transcript
linearization point. Before that point, Stop can discard the pending result.
After it, cancellation or provider failure cannot erase the exact tool result
already committed to the in-memory session transcript. The UI says only that
an answer was accepted and the agent is continuing; it does not promise
provider delivery before that commit.

Visible chat projects an assistant question and user answer only after a
matching successful generated tool result is present in the structured
session transcript. A merely proposed, invalid, declined, expired, cancelled,
or target-drifted question is not manufactured into chat history.

### Presentation, persistence, and audit

The desktop renders a distinct `INPUT NEEDED` card inside the existing agent
surface. It labels the question as untrusted model content, shows its expiry,
provides a dedicated single-line answer field plus explicit Send and Skip
actions, and warns that a response is not approval and must not contain
credentials. The card is an assertive accessible live region with named,
keyboard-reachable controls. It does not steal focus from an active terminal.

Questions and answers are retained only in the bounded in-memory agent
conversation and visible run snapshot. They are not written to the
capability/action audit, SQLite definitions, recovery, diagnostics, normal
logs, or provider credentials. Clearing the run removes them with the rest of
the native session transcript.

## Consequences

- The native .NET agent can pause one provider turn for a human clarification
  without a Node sidecar or any remote-machine component.
- A free-text “yes” can inform planning but cannot approve a command, bypass an
  approval card, change policy, or widen the target.
- Stale, duplicate, secret-shaped, malformed, late, or target-drifted
  responses fail closed.
- The first slice deliberately omits model-controlled choice widgets and
  durable clarification history. Those can be added only after concrete
  product need and a compatible bounded contract.

## Alternatives rejected

- Reusing the approval card would blur intent and authority and could make
  model text impersonate trusted authorization material.
- Treating the answer as a new top-level chat turn would abandon the exact
  pending proposal generation and complicate replay.
- Writing questions or answers into action audit would retain prompt content
  without providing authorization evidence.
- Launching a Node/Pi subprocess for interactive questions would violate the
  native in-process runtime decision and add an unnecessary lifecycle boundary.
