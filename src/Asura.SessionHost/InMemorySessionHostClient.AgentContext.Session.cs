using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private HostResult<AgentContextSnapshot>? AddSessionPanel(
        AgentTarget.ConnectionSession target,
        IReadOnlyDictionary<SessionId, SessionDescriptor> sessions,
        ICollection<AgentContextPanel> panels,
        int maximumPanelCount)
    {
        if (!sessions.TryGetValue(target.SessionId, out var session)
            || session.Lifecycle is SessionLifecycle.Closed or SessionLifecycle.Failed)
        {
            return NotFound<AgentContextSnapshot>("live session", 0);
        }

        if (panels.Count >= maximumPanelCount)
        {
            return InvalidAgentContext(
                "The target resolves to more panels than the request permits.",
                session.Revision);
        }

        var graphResult = _workspaceGraphs.Get(session.Owner.WorkspaceId);
        if (graphResult is HostResult<WorkspaceGraphSnapshot>.Failure rejected
            && rejected.Error.Code != HostErrorCode.NotFound)
        {
            return HostResult<AgentContextSnapshot>.Fail(
                rejected.Error,
                rejected.CurrentRevision);
        }

        var graph = graphResult is HostResult<WorkspaceGraphSnapshot>.Success success
            ? success.Value
            : null;

        if (graph is not null && graph.WindowId != session.Owner.WindowId)
        {
            return InvalidAgentContext(
                "The exact live session owner conflicts with the registered workspace graph.",
                session.Revision);
        }

        try
        {
            panels.Add(AgentContextPanel.ForExactSession(session, graph));
            return null;
        }
        catch (ArgumentException)
        {
            return InvalidAgentContext(
                "The exact live session has stale graph ownership metadata.",
                session.Revision);
        }
    }
}
