namespace Asura.Application;

/// <summary>
/// A typed file request and the proposal derived from that exact request and trusted file scope.
/// Only the trusted composer can create this immutable pairing.
/// </summary>
public sealed class AgentFileAction
{
    internal AgentFileAction(
        AgentFileRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentFileRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
