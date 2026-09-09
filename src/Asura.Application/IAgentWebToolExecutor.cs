using Asura.Core;

namespace Asura.Application;

public interface IAgentWebToolExecutor
{
    ValueTask<AgentWebToolExecutionResult> ExecuteAsync(
        WorkspaceInstanceId workspaceId,
        AgentWebToolRequest request,
        CancellationToken cancellationToken);
}
