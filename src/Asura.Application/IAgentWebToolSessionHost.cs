using Asura.Core;

namespace Asura.Application;

public interface IAgentWebToolSessionHost
{
    ValueTask<HostResult<AgentWebToolResult>> RunAgentWebToolAsync(
        AgentAuthorizationId authorizationId,
        AgentWebToolAction action,
        CancellationToken cancellationToken);
}
