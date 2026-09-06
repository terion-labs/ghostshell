using GhostShell.Core;

namespace GhostShell.Application;

public interface IAgentWebToolExecutor
{
    ValueTask<AgentWebToolExecutionResult> ExecuteAsync(
        WorkspaceInstanceId workspaceId,
        AgentWebToolRequest request,
        CancellationToken cancellationToken);
}
