namespace Asura.Application;

/// <summary>
/// A typed local-statistics observation paired with its governed proposal.
/// </summary>
public sealed class AgentStatisticsReadAction
{
    internal AgentStatisticsReadAction(
        AgentStatisticsReadRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentStatisticsReadRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
