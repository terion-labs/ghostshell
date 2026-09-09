namespace Asura.Application;

public sealed record AgentDockerControlAction(
    AgentDockerControlRequest Request,
    AgentActionProposal Proposal);

public sealed record AgentDockerControlResult(
    string ToolName,
    DockerContainerControlOutcome Outcome,
    string StableCode,
    bool Retryable);
