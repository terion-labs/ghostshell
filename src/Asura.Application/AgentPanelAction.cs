namespace Asura.Application;

/// <summary>
/// A typed panel request paired with the exact proposal derived by the trusted
/// composer. Provider code cannot construct this pairing.
/// </summary>
public sealed class AgentPanelAction
{
    internal AgentPanelAction(
        AgentPanelRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentPanelRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
