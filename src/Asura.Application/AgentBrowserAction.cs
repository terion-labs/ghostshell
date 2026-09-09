namespace Asura.Application;

/// <summary>
/// A typed browser request and the proposal derived from that exact request.
/// Only the trusted composer can create this immutable pairing.
/// </summary>
public sealed class AgentBrowserAction
{
    internal AgentBrowserAction(
        AgentBrowserRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentBrowserRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
