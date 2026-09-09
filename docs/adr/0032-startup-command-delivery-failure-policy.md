# ADR 0032: Startup-command delivery-failure policy

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0001](0001-terminal-session-and-shim-boundary.md)

## Context

A saved terminal panel can contain startup commands. Asura already treats
that command batch as a one-shot side effect: it uses one stable request and
idempotency identity, audits before writing, retries only a typed retryable
delivery failure while the same runtime instance remains live, and never
restores command side effects after a crash.

Always retrying is not appropriate for every connection. A user may prefer to
keep the terminal open for inspection after the first failed delivery rather
than let renderer recreation, reattachment, or reconnect trigger another
automatic attempt. The policy must be durable and visible without pretending
that transport acknowledgement reports the shell command's eventual exit
status.

## Decision

`PanelStartupBehavior` adds the closed
`StartupCommandDeliveryFailurePolicy` value:

- `RetryWhileLive` preserves the existing bounded retry behavior and is the
  backward-compatible default for payloads that omit the field.
- `StopAfterFirstDeliveryFailure` permits one delivery attempt. Its first typed
  delivery failure latches that opened terminal-panel instance, keeps the
  terminal session open, and prevents automatic redispatch for the rest of the
  instance.

Undefined values fail validation or deserialization. Only terminal panels may
store a non-default delivery-failure policy.

The saved-screen and workspace-only-tab editors expose the same closed,
keyboard-reachable selector beside terminal startup commands. Their help text
states that this policy governs delivery or acknowledgement failure, not a
command's exit status. A definition revision is copied into each opened runtime
instance; editing the durable definition cannot retarget an already-open
panel.

The stop latch is owned above replaceable native or managed renderer controls.
It therefore survives polling callbacks, renderer recreation, reattachment,
and connection reconnect within that panel instance. The visible error and
polite status distinguish a scheduled retry from a stopped automatic delivery.
Closing or disposing the panel cancels pending work.

That runtime-owned dispatch state is an atomic bundle of the
`PanelInstanceId`, a defensive copy of the command batch, the exact operation
context and idempotency key, the selected policy and retry clock, and the latest
typed outcome. A renderer contributes only its current session transport and
input lease. The state rejects a session request whose owner panel does not
match before it invokes the dispatcher or mutates its latch. Missing state
fails closed. The owning panel view model subscribes directly to state outcomes
so detaching or rebinding a control cannot lose the visible error, and panel
disposal cancels the state independently of renderer teardown.

`RetryWhileLive` keeps the exact batch request and idempotency key across
attempts and uses the existing capped 1, 2, then 5 second schedule. Neither
policy retries after confirmed delivery. If the write committed but completion
audit is uncertain, the confirmed-delivery state wins and the command batch is
not replayed. Audit persistence failure before the write remains a typed
failure.

Recovery continues to retain only the safe startup location. It never restores
startup commands or their delivery-failure latch because doing so could repeat
an unconfirmed side effect.

This policy says nothing about a command's process exit code. Shell completion,
per-command continuation, and stop/continue behavior after a nonzero exit
require a separate shell-integration contract and decision.

## Consequences

- Existing definitions retain their prior retry behavior without a migration.
- A user can choose fail-once delivery while keeping the failed terminal
  available for diagnosis.
- Native and managed renderer replacement cannot reset the user's choice or
  bypass the runtime-instance latch.
- The application does not claim knowledge of shell-command success from PTY
  delivery.

## Alternatives rejected

- Treating every delivery failure as permanently stopped would change existing
  saved screens silently.
- Keeping the latch only inside a renderer control would let recreation or
  reconnect retry despite the selected policy.
- Replaying startup commands during recovery cannot distinguish a lost
  acknowledgement from a side effect that already occurred.
- Inferring command success from terminal text or prompts would make correctness
  depend on untrusted, shell-specific output.
