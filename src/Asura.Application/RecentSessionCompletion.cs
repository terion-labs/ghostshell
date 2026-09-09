using Asura.Core;

namespace Asura.Application;

public sealed record RecentSessionCompletion
{
    public RecentSessionCompletion(
        SessionId sessionId,
        DateTimeOffset endedAt,
        RecentSessionOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(sessionId.Value))
        {
            throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        }

        if (!Enum.IsDefined(outcome) || outcome == RecentSessionOutcome.Active)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                "A completion requires a terminal recent-session outcome.");
        }

        SessionId = sessionId;
        EndedAt = endedAt.ToUniversalTime();
        Outcome = outcome;
    }

    public SessionId SessionId { get; }

    public DateTimeOffset EndedAt { get; }

    public RecentSessionOutcome Outcome { get; }
}
