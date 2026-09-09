namespace Asura.Application;

/// <summary>
/// Closed, presentation-safe evidence from one durable agent audit entry.
/// Raw storage rows, arguments, content, labels, and actor identifiers are
/// deliberately absent.
/// </summary>
public abstract record AgentRunAuditEntry
{
    private protected AgentRunAuditEntry(
        AgentActionDigest entryId,
        DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(entryId.Value))
        {
            throw new ArgumentException(
                "An audit entry requires a stable digest.",
                nameof(entryId));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An agent-run audit entry timestamp must be UTC.",
                nameof(occurredAtUtc));
        }

        EntryId = entryId;
        OccurredAtUtc = occurredAtUtc;
    }

    public AgentActionDigest EntryId { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}
