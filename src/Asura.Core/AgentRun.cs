namespace Asura.Core;

public enum AgentRunState
{
    Pending,
    Running,
    WaitingForApproval,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record AgentRun(
    AgentRunId Id,
    AgentTarget Target,
    string Goal,
    AgentRunState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    CommandBlockId? CommandBlockId = null)
{
    public AgentTarget Target { get; init; } =
        Target ?? throw new ArgumentNullException(nameof(Target));

    public AgentRun Start(DateTimeOffset startedAt)
    {
        EnsureState(AgentRunState.Pending);
        return this with { State = AgentRunState.Running, StartedAt = startedAt };
    }

    public AgentRun LinkCommand(CommandBlockId commandBlockId)
    {
        if (State is not (AgentRunState.Running or AgentRunState.WaitingForApproval))
        {
            throw new InvalidOperationException($"Cannot link a command while the run is {State}.");
        }

        return this with { CommandBlockId = commandBlockId };
    }

    public AgentRun Complete(DateTimeOffset finishedAt)
    {
        EnsureState(AgentRunState.Running);
        return this with { State = AgentRunState.Succeeded, FinishedAt = finishedAt };
    }

    public AgentRun Cancel(DateTimeOffset finishedAt)
    {
        if (State is not (AgentRunState.Running or AgentRunState.WaitingForApproval))
        {
            throw new InvalidOperationException($"Cannot cancel an agent run while it is {State}.");
        }

        return this with { State = AgentRunState.Cancelled, FinishedAt = finishedAt };
    }

    private void EnsureState(AgentRunState expected)
    {
        if (State != expected)
        {
            throw new InvalidOperationException($"Expected agent run state {expected}, but found {State}.");
        }
    }
}
