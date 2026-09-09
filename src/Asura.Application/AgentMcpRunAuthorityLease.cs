using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Unforgeable proof of one broker generation's live MCP launch authority.
/// The token is cancelled before that authority can be replaced or revoked.
/// </summary>
public sealed class AgentMcpRunAuthorityLease
{
    internal AgentMcpRunAuthorityLease(
        AgentRunId runId,
        ActorDescriptor agent,
        long policyGeneration,
        CancellationToken revocationToken)
    {
        RunId = runId;
        Agent = agent;
        PolicyGeneration = policyGeneration;
        RevocationToken = revocationToken;
    }

    public AgentRunId RunId { get; }

    public ActorDescriptor Agent { get; }

    public long PolicyGeneration { get; }

    public CancellationToken RevocationToken { get; }
}
