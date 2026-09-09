using Asura.Core;

namespace Asura.Application;

public interface IAgentGitSessionHost
{
    ValueTask<HostResult<GitAgentOperationResult>> RunAgentGitActionAsync(
        AgentAuthorizationId authorizationId,
        AgentGitAction action,
        CancellationToken cancellationToken);
}
