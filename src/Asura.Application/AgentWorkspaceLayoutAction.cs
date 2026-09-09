namespace Asura.Application;

/// <summary>
/// One typed layout mutation paired with its trusted proposal and graph binding.
/// </summary>
public sealed class AgentWorkspaceLayoutAction
{
    internal AgentWorkspaceLayoutAction(
        AgentWorkspaceLayoutRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentWorkspaceLayoutRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
