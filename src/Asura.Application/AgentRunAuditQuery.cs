using Asura.Core;

namespace Asura.Application;

public sealed record AgentRunAuditQuery
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    public AgentRunAuditQuery(
        AgentRunId runId,
        AgentRunAuditCursor? before = null,
        int pageSize = DefaultPageSize)
    {
        AgentRunRegistration.ValidateRunId(runId);
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                $"An agent-run audit page must contain between 1 and "
                + $"{MaximumPageSize} entries.");
        }

        RunId = runId;
        Before = before;
        PageSize = pageSize;
    }

    public AgentRunId RunId { get; }

    public AgentRunAuditCursor? Before { get; }

    public int PageSize { get; }
}
