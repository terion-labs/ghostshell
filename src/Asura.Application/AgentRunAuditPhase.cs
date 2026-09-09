using Asura.Core;

namespace Asura.Application;

public sealed record AgentRunAuditPhase
{
    public AgentRunAuditPhase(
        AuditOutcome outcome,
        ActorKind actorKind,
        DateTimeOffset occurredAtUtc)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (!Enum.IsDefined(actorKind))
        {
            throw new ArgumentOutOfRangeException(nameof(actorKind));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An agent-run audit phase timestamp must be UTC.",
                nameof(occurredAtUtc));
        }

        Outcome = outcome;
        ActorKind = actorKind;
        OccurredAtUtc = occurredAtUtc;
    }

    public AuditOutcome Outcome { get; }

    public ActorKind ActorKind { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}
