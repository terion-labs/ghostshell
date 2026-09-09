namespace Asura.Application;

public sealed class AgentWebToolAction
{
    internal AgentWebToolAction(
        AgentWebToolRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentWebToolRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
