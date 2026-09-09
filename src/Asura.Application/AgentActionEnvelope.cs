using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Carries the run-owned identity and lifetime that every prepared agent action must bind.
/// It contains no operation arguments or reusable execution authority.
/// </summary>
public sealed record AgentActionEnvelope
{
    public AgentActionEnvelope(
        AgentActionId actionId,
        AgentRunId runId,
        ActorDescriptor actor,
        long policyGeneration,
        DateTimeOffset createdAtUtc,
        DateTimeOffset deadlineUtc)
    {
        RequireIdentifier(actionId.Value, nameof(actionId));
        AgentRunRegistration.ValidateRunId(runId);
        Actor = AgentRunRegistration.ValidateAgent(actor);
        ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
        if (createdAtUtc.Offset != TimeSpan.Zero
            || deadlineUtc.Offset != TimeSpan.Zero
            || deadlineUtc <= createdAtUtc
            || deadlineUtc - createdAtUtc > AgentActionProposal.MaximumLifetime)
        {
            throw new ArgumentException(
                "Agent action timestamps must be ordered UTC values with a bounded lifetime.",
                nameof(deadlineUtc));
        }

        ActionId = actionId;
        RunId = runId;
        PolicyGeneration = policyGeneration;
        CreatedAtUtc = createdAtUtc;
        DeadlineUtc = deadlineUtc;
    }

    public AgentActionId ActionId { get; }

    public AgentRunId RunId { get; }

    public ActorDescriptor Actor { get; }

    public long PolicyGeneration { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset DeadlineUtc { get; }

    private static void RequireIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(value) > 256)
        {
            throw new ArgumentException(
                "An agent action identifier must be printable and bounded.",
                parameterName);
        }
    }
}
