using Asura.Core;

namespace Asura.Application;

public interface IAgentDockerSessionHost
{
    ValueTask<HostResult<AgentDockerReadResult>> RunAgentDockerReadAsync(
        AgentAuthorizationId authorizationId,
        AgentDockerReadAction action,
        CancellationToken cancellationToken);

    ValueTask<HostResult<AgentDockerControlResult>> RunAgentDockerControlAsync(
        AgentAuthorizationId authorizationId,
        AgentDockerControlAction action,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(HostResult<AgentDockerControlResult>.Fail(
            new HostError(
                HostErrorCode.CapabilityNotSupported,
                "docker_container_control_unavailable",
                "The governed Docker lifecycle bridge is not available."),
            0));
}
