namespace Asura.Application;

public sealed record AgentGitAction(
    AgentGitRequest Request,
    AgentActionProposal Proposal);
