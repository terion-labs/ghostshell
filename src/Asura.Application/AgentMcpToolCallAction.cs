namespace Asura.Application;

/// <summary>
/// A typed MCP call paired with the generic trusted broker proposal.
/// </summary>
public sealed class AgentMcpToolCallAction
{
    internal AgentMcpToolCallAction(
        AgentMcpToolCallRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentMcpToolCallRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
