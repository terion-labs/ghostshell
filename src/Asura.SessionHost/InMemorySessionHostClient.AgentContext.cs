using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<AgentContextSnapshot>> InspectAgentContextAsync(
        AgentContextRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        var invalid = ValidateContext<AgentContextSnapshot>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentContextSnapshot>(0);
        }

        try
        {
            ThrowIfDisposed();
            invalid = ValidateContext<AgentContextSnapshot>(context, cancellationToken, 0);
            if (invalid is not null)
            {
                return invalid;
            }

            HostedSession[] hostedSessions;
            lock (_gate)
            {
                hostedSessions = [.. _sessions.Values];
            }

            var sessions = hostedSessions
                .Select(session => session.Snapshot().Descriptor)
                .ToDictionary(session => session.Id);
            return ResolveAgentContext(request, sessions);
        }
        finally
        {
            _sessionGraphGate.Release();
        }
    }

    private HostResult<AgentContextSnapshot> ResolveAgentContext(
        AgentContextRequest request,
        IReadOnlyDictionary<SessionId, SessionDescriptor> sessions)
    {
        var graphs = new Dictionary<WorkspaceInstanceId, WorkspaceGraphSnapshot>();
        var panels = new List<AgentContextPanel>();
        var failure = ResolveAgentTarget(request, sessions, graphs, panels);
        if (failure is not null)
        {
            return failure;
        }

        if (panels.Count > request.MaximumPanelCount)
        {
            return InvalidAgentContext(
                "The target resolves to more panels than the request permits.",
                panels.Max(panel => panel.WorkspaceRevision));
        }

        try
        {
            var snapshot = new AgentContextSnapshot(
                request.Target,
                panels,
                _timeProvider.GetUtcNow());
            return HostResult<AgentContextSnapshot>.Succeed(snapshot, snapshot.Revision);
        }
        catch (ArgumentException)
        {
            return InvalidAgentContext(
                "The resolved agent context contains invalid bounded metadata.",
                panels.Count == 0 ? 0 : panels.Max(panel => panel.WorkspaceRevision));
        }
    }

    private HostResult<AgentContextSnapshot>? ResolveAgentTarget(
        AgentContextRequest request,
        IReadOnlyDictionary<SessionId, SessionDescriptor> sessions,
        IDictionary<WorkspaceInstanceId, WorkspaceGraphSnapshot> graphs,
        ICollection<AgentContextPanel> panels) =>
        request.Target switch
        {
            AgentTarget.Panel target => AddExactPanel(
                target,
                sessions,
                graphs,
                panels,
                request.MaximumPanelCount),
            AgentTarget.ConnectionSession target => AddSessionPanel(
                target,
                sessions,
                panels,
                request.MaximumPanelCount),
            AgentTarget.OpenTab target => AddTabPanels(
                target,
                sessions,
                graphs,
                panels,
                request.MaximumPanelCount),
            AgentTarget.Workspace target => AddWorkspacePanels(
                target,
                sessions,
                graphs,
                panels,
                request.MaximumPanelCount),
            AgentTarget.SelectedPanels target => AddSelectedPanels(
                target,
                sessions,
                graphs,
                panels,
                request.MaximumPanelCount),
            _ => InvalidAgentContext("The agent target kind is not supported.", 0),
        };
}
