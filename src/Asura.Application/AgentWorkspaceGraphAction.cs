namespace Asura.Application;

/// <summary>
/// A typed read-only graph request paired with the exact proposal produced by
/// the trusted composer. Provider code cannot construct this pairing.
/// </summary>
public sealed class AgentWorkspaceGraphAction
{
    internal AgentWorkspaceGraphAction(
        AgentWorkspaceGraphRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentWorkspaceGraphRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
