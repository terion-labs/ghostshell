using System.Collections.ObjectModel;

namespace Asura.Application;

public sealed record AgentRunAuditPage
{
    public AgentRunAuditPage(
        IEnumerable<AgentRunAuditEntry> entries,
        AgentRunAuditCursor? next)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copies = entries
            .Select(entry => entry ?? throw new ArgumentException(
                "An agent-run audit page cannot contain null entries.",
                nameof(entries)))
            .ToArray();
        if (copies.Length > AgentRunAuditQuery.MaximumPageSize)
        {
            throw new ArgumentException(
                "An agent-run audit page exceeds its entry limit.",
                nameof(entries));
        }

        Entries = new ReadOnlyCollection<AgentRunAuditEntry>(copies);
        Next = next;
    }

    public IReadOnlyList<AgentRunAuditEntry> Entries { get; }

    public AgentRunAuditCursor? Next { get; }
}
