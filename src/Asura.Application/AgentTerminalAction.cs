namespace Asura.Application;

/// <summary>
/// A typed terminal request and the proposal derived from that exact request.
/// Only the trusted composer can create this immutable pairing.
/// </summary>
public sealed class AgentTerminalAction
{
    internal AgentTerminalAction(
        AgentTerminalRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentTerminalRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
