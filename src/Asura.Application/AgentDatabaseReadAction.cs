namespace Asura.Application;

/// <summary>
/// One typed relational or Redis observation paired with its governed proposal.
/// </summary>
public sealed class AgentDatabaseReadAction
{
    internal AgentDatabaseReadAction(
        AgentDatabaseReadRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentDatabaseReadRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
