using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Execution-time comparison evidence recomputed from a prepared typed action and a fresh exact
/// terminal context. It carries no approval presentation, raw arguments, or reusable authority.
/// </summary>
public sealed class AgentActionExecutionBinding
{
    internal AgentActionExecutionBinding(
        AgentActionId actionId,
        AgentRunId runId,
        ActorId actorId,
        string toolName,
        AgentTarget target,
        AgentActionDigest targetIdentity,
        AgentActionDigest targetFingerprint,
        AgentActionDigest argumentDigest,
        long policyGeneration)
    {
        ActionId = actionId;
        RunId = runId;
        ActorId = actorId;
        ToolName = toolName;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        TargetIdentity = targetIdentity;
        TargetFingerprint = targetFingerprint;
        ArgumentDigest = argumentDigest;
        PolicyGeneration = policyGeneration;
    }

    public AgentActionId ActionId { get; }

    public AgentRunId RunId { get; }

    public ActorId ActorId { get; }

    public string ToolName { get; }

    public AgentTarget Target { get; }

    public AgentActionDigest TargetIdentity { get; }

    public AgentActionDigest TargetFingerprint { get; }

    public AgentActionDigest ArgumentDigest { get; }

    public long PolicyGeneration { get; }

    internal static AgentActionExecutionBinding FromProposal(
        AgentActionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return new AgentActionExecutionBinding(
            proposal.Id,
            proposal.RunId,
            proposal.Actor.Id,
            proposal.ToolName,
            proposal.Target,
            proposal.TargetIdentity,
            proposal.TargetFingerprint,
            proposal.ArgumentDigest,
            proposal.PolicyGeneration);
    }
}
