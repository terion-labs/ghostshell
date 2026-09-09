using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private bool TryGetAgentGraph(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId,
        IDictionary<WorkspaceInstanceId, WorkspaceGraphSnapshot> graphs,
        out WorkspaceGraphSnapshot graph,
        out HostResult<AgentContextSnapshot>? failure)
    {
        if (graphs.TryGetValue(workspaceId, out graph!))
        {
            if (graph.WindowId != windowId)
            {
                failure = NotFound<AgentContextSnapshot>(
                    "workspace graph owner",
                    graph.Revision);
                return false;
            }

            failure = null;
            return true;
        }

        var result = _workspaceGraphs.Get(workspaceId);
        if (result is HostResult<WorkspaceGraphSnapshot>.Failure rejected)
        {
            graph = null!;
            failure = HostResult<AgentContextSnapshot>.Fail(
                rejected.Error,
                rejected.CurrentRevision);
            return false;
        }

        graph = ((HostResult<WorkspaceGraphSnapshot>.Success)result).Value;
        if (graph.WindowId != windowId)
        {
            failure = NotFound<AgentContextSnapshot>(
                "workspace graph owner",
                graph.Revision);
            return false;
        }

        graphs.Add(workspaceId, graph);
        failure = null;
        return true;
    }

    private static HostResult<AgentContextSnapshot> InvalidAgentContext(
        string message,
        long revision) =>
        HostResult<AgentContextSnapshot>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            revision);
}
