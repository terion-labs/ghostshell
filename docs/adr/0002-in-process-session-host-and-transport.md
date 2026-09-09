# ADR 0002: In-process desktop session host behind a protocol-shaped client

- Status: Accepted
- Date: 2026-07-22

## Context

Desktop v1 owns sessions in one process, while later web, CLI, ACP, and A2A clients require a standalone host. View models must not be rewritten when transport changes.

## Decision

Desktop uses an in-process `InMemorySessionHostClient` implementing `ISessionHostClient`. Application operations are typed and carry stable IDs, actor, request ID, optional expected revision, idempotency key, cancellation ID, and deadline. A separate `Asura.Protocol` assembly defines versioned serializable request, response, error, event, capability, and stream envelopes without UI or engine types.

The host owns session registry state, monotonically increasing per-session revisions and event sequences, bounded replay, explicit resynchronization, attachments, and one input lease per session. Human input may preempt an agent lease; an agent cannot preempt a human lease. Desktop owner close terminates sessions after policy checks. Client disconnect detaches attachments only, which preserves the future server distinction.

The executable composition root is `Asura.Desktop`. `Asura.App` is presentation-only, `Asura.SessionHost` contains runtime behavior, and concrete engines are registered only by the composition root.

## Consequences

- Project dependency tests can enforce presentation and domain boundaries.
- A socket or authenticated WebSocket client can later implement the same application client.
- Bounded event history requires clients to handle `ResynchronizationRequired` explicitly.
- Cross-session close cannot be transactionally atomic after engine calls begin; denial and preflight are mutation-free, while execution reports every per-session outcome.

## Alternatives rejected

- Direct engine injection into view models would preserve an in-process assumption.
- A generic `Execute(string, object)` API would erase type safety and stable errors.
- Full event sourcing is unnecessary for ordinary runtime snapshots and durable definitions.
