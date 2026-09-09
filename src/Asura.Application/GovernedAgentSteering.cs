using Asura.Core;

namespace Asura.Application;

/// <summary>
/// One human-authored update for the actively streaming initial provider
/// generation of a governed turn. The run and expected generation prevent a
/// stale presentation event from steering a later turn in the same run.
/// </summary>
public sealed record GovernedAgentSteering
{
    public const int MaximumUpdateLength =
        GovernedAgentPrompt.MaximumMessageLength;

    public GovernedAgentSteering(
        AgentRunId runId,
        long expectedGeneration,
        string update)
    {
        AgentRunRegistration.ValidateRunId(runId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            expectedGeneration);
        ArgumentException.ThrowIfNullOrWhiteSpace(update);
        if (update.Length > MaximumUpdateLength)
        {
            throw new ArgumentException(
                "The agent steering update exceeds its character limit.",
                nameof(update));
        }

        RunId = runId;
        ExpectedGeneration = expectedGeneration;
        Update = string.Concat(update);
    }

    public AgentRunId RunId { get; }

    public long ExpectedGeneration { get; }

    public string Update { get; }
}
