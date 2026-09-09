namespace Asura.Application;

/// <summary>
/// One prepared Docker observation whose exact target and typed argument digest
/// are bound into the ordinary one-action authorization flow.
/// </summary>
public sealed record AgentDockerReadAction(
    AgentDockerReadRequest Request,
    AgentActionProposal Proposal);
