namespace Asura.Application;

/// <summary>
/// A typed local-process observation paired with the exact governed proposal
/// derived by the trusted composer.
/// </summary>
public sealed class AgentProcessListAction
{
    internal AgentProcessListAction(
        AgentProcessListRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentProcessListRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
