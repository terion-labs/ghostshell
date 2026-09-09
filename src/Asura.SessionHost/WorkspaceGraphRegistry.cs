using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

internal sealed record LiveWorkspaceSession(
    SessionDescriptor Descriptor,
    PanelSessionRole Role);

internal sealed class WorkspaceGraphRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<WindowInstanceId, ClientId> _clientByWindow = [];
    /// <summary>
    /// The workspaces each window holds — several, not one.
    ///
    /// This was one workspace per window, and registering a second evicted the
    /// first: the graph was removed, the client saw the removal and closed what
    /// it believed the host had ended, and every session in the workspace you
    /// had just switched away from died. A window showing one workspace at a
    /// time is a presentation fact; it was never a reason for the host to
    /// forget the others.
    /// </summary>
    private readonly Dictionary<WindowInstanceId, HashSet<WorkspaceInstanceId>> _workspacesByWindow = [];
    private readonly Dictionary<WorkspaceInstanceId, HostedWorkspaceGraph> _workspaces = [];
    private readonly int _eventRetention;
    private readonly TimeProvider _timeProvider;

    public WorkspaceGraphRegistry(
        int eventRetention,
        TimeProvider timeProvider)
    {
        if (eventRetention < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(eventRetention));
        }

        ArgumentNullException.ThrowIfNull(timeProvider);
        _eventRetention = eventRetention;
        _timeProvider = timeProvider;
    }

    public long CurrentRevision(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            return _workspaces.TryGetValue(workspaceId, out var graph)
                ? graph.Revision
                : 0;
        }
    }

    public long CurrentRegistrationRevision(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            _ = windowId;
            return _workspaces.TryGetValue(workspaceId, out var requested)
                ? requested.Revision
                : 0;
        }
    }

    public HostResult<WorkspaceGraphSnapshot> RegisterOrReplace(
        RegisterWorkspaceGraphRequest request,
        ClientId? ownerClientId,
        long? expectedRevision,
        IReadOnlyList<LiveWorkspaceSession> liveSessions)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(liveSessions);
        lock (_gate)
        {
            _workspaces.TryGetValue(request.Workspace.Id, out var requested);
            if (requested is not null && requested.WindowId != request.WindowId)
            {
                return HostResult<WorkspaceGraphSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.InvalidRequest,
                        "The workspace instance ID is already owned by another window."),
                    requested.Revision);
            }

            var currentRevision = requested?.Revision ?? 0;

            if (ownerClientId is { } clientId
                && _clientByWindow.TryGetValue(request.WindowId, out var currentClientId)
                && currentClientId != clientId)
            {
                return HostResult<WorkspaceGraphSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.InvalidRequest,
                        "The window runtime graph is already owned by another client."),
                    currentRevision);
            }

            if (expectedRevision is { } expected && expected != currentRevision)
            {
                return RevisionConflict<WorkspaceGraphSnapshot>(currentRevision, expected);
            }

            if (!TryReconcileSessionLinks(
                    request.WindowId,
                    request.Workspace,
                    liveSessions,
                    requested?.Snapshot().Workspace,
                    out var reconciledWorkspace,
                    out var reconciliationError))
            {
                return HostResult<WorkspaceGraphSnapshot>.Fail(
                    reconciliationError!,
                    currentRevision);
            }

            if (requested is not null)
            {
                if (ownerClientId is { } replacementClientId)
                {
                    _clientByWindow[request.WindowId] = replacementClientId;
                }

                return requested.Replace(reconciledWorkspace, expectedRevision);
            }

            var replacement = new HostedWorkspaceGraph(
                request.WindowId,
                reconciledWorkspace,
                _eventRetention,
                _timeProvider);
            _workspaces.Add(replacement.WorkspaceId, replacement);
            WorkspacesOfUnsafe(request.WindowId).Add(replacement.WorkspaceId);
            if (ownerClientId is { } registeringClientId)
            {
                _clientByWindow[request.WindowId] = registeringClientId;
            }

            var snapshot = replacement.Snapshot();
            return HostResult<WorkspaceGraphSnapshot>.Succeed(snapshot, snapshot.Revision);
        }
    }

    public HostResult<WorkspaceGraphSnapshot> Get(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(workspaceId, out var graph))
            {
                return NotFound();
            }

            var snapshot = graph.Snapshot();
            return HostResult<WorkspaceGraphSnapshot>.Succeed(snapshot, snapshot.Revision);
        }
    }

    public HostResult<Unit> Unregister(
        UnregisterWorkspaceGraphRequest request,
        ClientId? ownerClientId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(request.WorkspaceId, out var graph)
                || graph.WindowId != request.WindowId)
            {
                return HostResult<Unit>.Fail(
                    HostError.Create(HostErrorCode.NotFound, "The workspace graph was not found."),
                    0);
            }

            var currentRevision = graph.Revision;
            if (ownerClientId is { } clientId
                && _clientByWindow.TryGetValue(request.WindowId, out var currentClientId)
                && currentClientId != clientId)
            {
                return HostResult<Unit>.Fail(
                    HostError.Create(
                        HostErrorCode.InvalidRequest,
                        "The window runtime graph is owned by another client."),
                    currentRevision);
            }

            if (expectedRevision is { } expected && expected != currentRevision)
            {
                return RevisionConflict<Unit>(currentRevision, expected);
            }

            ForgetWorkspaceUnsafe(request.WindowId, request.WorkspaceId);
            var removedRevision = graph.Remove();
            return HostResult<Unit>.Succeed(Unit.Value, removedRevision);
        }
    }

    public HostResult<WorkspaceGraphSnapshot> ActivateTab(
        ActivateWorkspaceTabRequest request,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            return _workspaces.TryGetValue(request.WorkspaceId, out var graph)
                ? graph.ActivateTab(request.TabId, expectedRevision)
                : NotFound();
        }
    }

    public HostResult<WorkspaceGraphSnapshot> ActivatePanel(
        ActivateWorkspacePanelRequest request,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            return _workspaces.TryGetValue(request.WorkspaceId, out var graph)
                ? graph.ActivatePanel(request.TabId, request.PanelId, expectedRevision)
                : NotFound();
        }
    }

    public HostResult<WorkspaceGraphTransferReceipt> TransferTab(
        TransferWorkspaceTabRequest request,
        ClientId clientId,
        IReadOnlyList<LiveWorkspaceSession> liveSessions)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(liveSessions);
        lock (_gate)
        {
            if (ResolveTransferGraphs(
                    request.SourceWindowId,
                    request.Source.Id,
                    request.ExpectedSourceRevision,
                    request.DestinationWindowId,
                    request.Destination.Id,
                    request.ExpectedDestinationRevision,
                    clientId)
                is not { } graphs)
            {
                return TransferFailure(
                    request.Source.Id,
                    request.Destination.Id,
                    "The exact source and destination ownership receipts are no longer current.");
            }

            var sourceBefore = graphs.Source.Snapshot().Workspace;
            var destinationBefore = graphs.Destination.Snapshot().Workspace;
            var moved = sourceBefore.Tabs.SingleOrDefault(tab => tab.Id == request.TabId);
            if (moved is null
                || sourceBefore.Tabs.Count == 1
                || destinationBefore.Tabs.Any(tab => tab.Id == request.TabId)
                || !WorkspaceTitleMatches(sourceBefore, request.Source)
                || !WorkspaceTitleMatches(destinationBefore, request.Destination)
                || !SequenceEqual(
                    request.Source.Tabs,
                    sourceBefore.Tabs.Where(tab => tab.Id != request.TabId),
                    TabEquivalent)
                || !TabEquivalent(
                    moved,
                    request.Destination.Tabs.SingleOrDefault(tab => tab.Id == request.TabId))
                || !SequenceEqual(
                    request.Destination.Tabs.Where(tab => tab.Id != request.TabId),
                    destinationBefore.Tabs,
                    TabEquivalent))
            {
                return InvalidTransfer(
                    graphs,
                    "A tab transfer must move exactly one unchanged tab and leave every other topology unchanged.");
            }

            var ownership = liveSessions
                .Where(session =>
                    session.Descriptor.Owner.WorkspaceId == sourceBefore.Id
                    && session.Descriptor.Owner.TabId == request.TabId)
                .Select(session =>
                {
                    var source = session.Descriptor.Owner;
                    var destination = source with
                    {
                        WindowId = request.DestinationWindowId,
                        WorkspaceId = request.Destination.Id,
                    };
                    return new SessionOwnershipTransferReceipt(
                        session.Descriptor.Id,
                        source,
                        destination);
                })
                .ToArray();

            return CommitTransfer(
                graphs,
                request.Source,
                request.Destination,
                request.TabId,
                null,
                ownership);
        }
    }

    public HostResult<WorkspaceGraphTransferReceipt> TransferPanel(
        TransferWorkspacePanelRequest request,
        ClientId clientId,
        IReadOnlyList<LiveWorkspaceSession> liveSessions)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(liveSessions);
        lock (_gate)
        {
            if (ResolveTransferGraphs(
                    request.SourceWindowId,
                    request.Source.Id,
                    request.ExpectedSourceRevision,
                    request.DestinationWindowId,
                    request.Destination.Id,
                    request.ExpectedDestinationRevision,
                    clientId)
                is not { } graphs)
            {
                return TransferFailure(
                    request.Source.Id,
                    request.Destination.Id,
                    "The exact source and destination ownership receipts are no longer current.");
            }

            var sourceBefore = graphs.Source.Snapshot().Workspace;
            var destinationBefore = graphs.Destination.Snapshot().Workspace;
            var sourceTab = sourceBefore.Tabs.SingleOrDefault(tab => tab.Id == request.SourceTabId);
            var destinationTab = destinationBefore.Tabs.SingleOrDefault(
                tab => tab.Id == request.DestinationTabId);
            var moved = sourceTab?.Panels.SingleOrDefault(panel => panel.Id == request.PanelId);
            var sourceAfterTab = request.Source.Tabs.SingleOrDefault(tab => tab.Id == request.SourceTabId);
            var destinationAfterTab = request.Destination.Tabs.SingleOrDefault(
                tab => tab.Id == request.DestinationTabId);
            if (sourceTab is null
                || destinationTab is null
                || moved is null
                || sourceTab.Panels.Count == 1
                || destinationBefore.Tabs.SelectMany(tab => tab.Panels).Any(
                    panel => panel.Id == request.PanelId)
                || sourceAfterTab is null
                || destinationAfterTab is null
                || !WorkspaceTitleMatches(sourceBefore, request.Source)
                || !WorkspaceTitleMatches(destinationBefore, request.Destination)
                || !WorkspaceExceptTabMatches(
                    sourceBefore,
                    request.Source,
                    request.SourceTabId)
                || !WorkspaceExceptTabMatches(
                    destinationBefore,
                    request.Destination,
                    request.DestinationTabId)
                || !TabWithoutPanelMatches(sourceTab, sourceAfterTab, request.PanelId)
                || !TabWithAddedPanelMatches(destinationTab, destinationAfterTab, moved))
            {
                return InvalidTransfer(
                    graphs,
                    "A panel transfer must move exactly one unchanged panel and leave every other topology unchanged.");
            }

            var ownership = liveSessions
                .Where(session =>
                    session.Descriptor.Owner.WorkspaceId == sourceBefore.Id
                    && session.Descriptor.Owner.TabId == request.SourceTabId
                    && session.Descriptor.Owner.PanelId == request.PanelId)
                .Select(session =>
                {
                    var source = session.Descriptor.Owner;
                    var destination = source with
                    {
                        WindowId = request.DestinationWindowId,
                        WorkspaceId = request.Destination.Id,
                        TabId = request.DestinationTabId,
                    };
                    return new SessionOwnershipTransferReceipt(
                        session.Descriptor.Id,
                        source,
                        destination);
                })
                .ToArray();

            return CommitTransfer(
                graphs,
                request.Source,
                request.Destination,
                request.SourceTabId,
                request.PanelId,
                ownership);
        }
    }

    public HostResult<WorkspaceGraphSnapshot>? ValidateSessionOwner(
        SessionOwner owner,
        PanelKind kind,
        PanelSessionRole role = PanelSessionRole.Primary)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (RejectUnknownOwnerWorkspaceUnsafe(owner, out var graph) is { } rejection)
            {
                return rejection;
            }

            if (graph is null)
            {
                return null;
            }

            var ownerKind = OwnerPanelKind(kind, role);
            return ownerKind is { } expectedKind
                ? graph.ValidateSessionOwner(owner.TabId, owner.PanelId, expectedKind)
                : InvalidSessionOwner(
                    graph.Revision,
                    "That session kind cannot be embedded in another panel.");
        }
    }

    public HostResult<WorkspaceGraphSnapshot>? LinkSession(
        SessionOwner owner,
        PanelKind kind,
        SessionId sessionId,
        PanelSessionRole role = PanelSessionRole.Primary)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (RejectUnknownOwnerWorkspaceUnsafe(owner, out var graph) is { } rejection)
            {
                return rejection;
            }

            if (graph is null)
            {
                return null;
            }

            var ownerKind = OwnerPanelKind(kind, role);
            if (ownerKind is null)
            {
                return InvalidSessionOwner(
                    graph.Revision,
                    "That session kind cannot be embedded in another panel.");
            }

            var validated = graph.ValidateSessionOwner(
                owner.TabId,
                owner.PanelId,
                ownerKind.Value);
            return role == PanelSessionRole.Embedded
                ? validated
                : graph.LinkSession(owner.TabId, owner.PanelId, kind, sessionId);
        }
    }

    public void UnlinkSession(
        SessionOwner owner,
        PanelKind kind,
        SessionId sessionId,
        PanelSessionRole role = PanelSessionRole.Primary)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (role == PanelSessionRole.Embedded
                || !_workspaces.TryGetValue(owner.WorkspaceId, out var graph)
                || graph.WindowId != owner.WindowId)
            {
                return;
            }

            graph.UnlinkSession(owner.TabId, owner.PanelId, kind, sessionId);
        }
    }

    public bool TryGetWatchSource(
        WorkspaceInstanceId workspaceId,
        out HostedWorkspaceGraph graph)
    {
        lock (_gate)
        {
            return _workspaces.TryGetValue(workspaceId, out graph!);
        }
    }

    /// <summary>
    /// Drops one workspace's graph, leaving the rest of its window's alone.
    /// Unlike <see cref="Unregister"/> this asks nothing of the caller: it
    /// follows a close the host has already carried out, so there is no
    /// ownership left to check and no revision left to conflict with.
    /// </summary>
    public void RemoveWorkspace(WorkspaceInstanceId workspaceId)
    {
        HostedWorkspaceGraph? graph;
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(workspaceId, out graph))
            {
                return;
            }

            ForgetWorkspaceUnsafe(graph.WindowId, workspaceId);
        }

        graph.Remove();
    }

    public void RemoveWindow(WindowInstanceId windowId)
    {
        lock (_gate)
        {
            foreach (var graph in RemoveWindowUnsafe(windowId))
            {
                graph.Remove();
            }
        }
    }

    public void RemoveClient(ClientId clientId)
    {
        HostedWorkspaceGraph[] graphs;
        lock (_gate)
        {
            var windows = _clientByWindow
                .Where(pair => pair.Value == clientId)
                .Select(pair => pair.Key)
                .ToArray();
            var removed = new List<HostedWorkspaceGraph>(windows.Length);
            foreach (var windowId in windows)
            {
                removed.AddRange(RemoveWindowUnsafe(windowId));
            }

            graphs = [.. removed];
        }

        foreach (var graph in graphs)
        {
            graph.Remove();
        }
    }

    public void Dispose()
    {
        HostedWorkspaceGraph[] graphs;
        lock (_gate)
        {
            graphs = [.. _workspaces.Values];
            _clientByWindow.Clear();
            _workspacesByWindow.Clear();
            _workspaces.Clear();
        }

        foreach (var graph in graphs)
        {
            graph.Remove();
        }
    }

    /// <summary>
    /// Says whether a session owner names a workspace this registry can accept.
    ///
    /// A workspace it has never heard of is only acceptable when the owner's
    /// window is unmanaged too — the Quick Terminal opens sessions before any
    /// graph exists. Once a window has graphs, an owner naming a workspace that
    /// is not among them is naming nothing, and a session must not be created
    /// for it.
    /// </summary>
    private HostResult<WorkspaceGraphSnapshot>? RejectUnknownOwnerWorkspaceUnsafe(
        SessionOwner owner,
        out HostedWorkspaceGraph? graph)
    {
        if (_workspaces.TryGetValue(owner.WorkspaceId, out var found))
        {
            graph = found;
            return found.WindowId == owner.WindowId
                ? null
                : InvalidSessionOwner(
                    found.Revision,
                    "The session owner workspace belongs to another window.");
        }

        graph = null;
        return _workspacesByWindow.ContainsKey(owner.WindowId)
            ? InvalidSessionOwner(
                0,
                "The session owner workspace is not registered in the owner window.")
            : null;
    }

    private HashSet<WorkspaceInstanceId> WorkspacesOfUnsafe(WindowInstanceId windowId)
    {
        if (!_workspacesByWindow.TryGetValue(windowId, out var workspaces))
        {
            workspaces = [];
            _workspacesByWindow.Add(windowId, workspaces);
        }

        return workspaces;
    }

    /// <summary>
    /// Drops one workspace, and the window with it once it holds no more. The
    /// client ownership entry belongs to the window, so it outlives any single
    /// workspace closing.
    /// </summary>
    private void ForgetWorkspaceUnsafe(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId)
    {
        _workspaces.Remove(workspaceId);
        if (!_workspacesByWindow.TryGetValue(windowId, out var workspaces))
        {
            return;
        }

        workspaces.Remove(workspaceId);
        if (workspaces.Count == 0)
        {
            _workspacesByWindow.Remove(windowId);
            _clientByWindow.Remove(windowId);
        }
    }

    private List<HostedWorkspaceGraph> RemoveWindowUnsafe(WindowInstanceId windowId)
    {
        List<HostedWorkspaceGraph> removed = [];
        if (!_workspacesByWindow.Remove(windowId, out var workspaces))
        {
            _clientByWindow.Remove(windowId);
            return removed;
        }

        foreach (var workspaceId in workspaces)
        {
            if (_workspaces.Remove(workspaceId, out var graph))
            {
                removed.Add(graph);
            }
        }

        _clientByWindow.Remove(windowId);
        return removed;
    }

    private TransferGraphs? ResolveTransferGraphs(
        WindowInstanceId sourceWindowId,
        WorkspaceInstanceId sourceWorkspaceId,
        long expectedSourceRevision,
        WindowInstanceId destinationWindowId,
        WorkspaceInstanceId destinationWorkspaceId,
        long expectedDestinationRevision,
        ClientId clientId)
    {
        if (!_workspaces.TryGetValue(sourceWorkspaceId, out var source)
            || !_workspaces.TryGetValue(destinationWorkspaceId, out var destination)
            || source == destination
            || source.WindowId != sourceWindowId
            || destination.WindowId != destinationWindowId
            || source.Revision != expectedSourceRevision
            || destination.Revision != expectedDestinationRevision
            || !_clientByWindow.TryGetValue(sourceWindowId, out var sourceClient)
            || !_clientByWindow.TryGetValue(destinationWindowId, out var destinationClient)
            || sourceClient != clientId
            || destinationClient != clientId)
        {
            return null;
        }

        return new TransferGraphs(source, destination);
    }

    private HostResult<WorkspaceGraphTransferReceipt> CommitTransfer(
        TransferGraphs graphs,
        WorkspaceInstance source,
        WorkspaceInstance destination,
        TabInstanceId tabId,
        PanelInstanceId? panelId,
        IReadOnlyList<SessionOwnershipTransferReceipt> ownership)
    {
        var sourceReceipt = graphs.Source.CommitTransfer(source, tabId, panelId);
        var destinationReceipt = graphs.Destination.CommitTransfer(destination, tabId, panelId);
        var receipt = new WorkspaceGraphTransferReceipt(
            Guid.NewGuid(),
            sourceReceipt,
            destinationReceipt,
            tabId,
            panelId,
            ownership);
        return HostResult<WorkspaceGraphTransferReceipt>.Succeed(
            receipt,
            Math.Max(sourceReceipt.Revision, destinationReceipt.Revision));
    }

    private HostResult<WorkspaceGraphTransferReceipt> TransferFailure(
        WorkspaceInstanceId sourceId,
        WorkspaceInstanceId destinationId,
        string message)
    {
        var sourceRevision = _workspaces.TryGetValue(sourceId, out var source)
            ? source.Revision
            : 0;
        var destinationRevision = _workspaces.TryGetValue(destinationId, out var destination)
            ? destination.Revision
            : 0;
        return HostResult<WorkspaceGraphTransferReceipt>.Fail(
            HostError.Create(HostErrorCode.RevisionConflict, message),
            Math.Max(sourceRevision, destinationRevision));
    }

    private static HostResult<WorkspaceGraphTransferReceipt> InvalidTransfer(
        TransferGraphs graphs,
        string message) =>
        HostResult<WorkspaceGraphTransferReceipt>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            Math.Max(graphs.Source.Revision, graphs.Destination.Revision));

    private static bool WorkspaceTitleMatches(
        WorkspaceInstance expected,
        WorkspaceInstance actual) =>
        string.Equals(expected.Title, actual.Title, StringComparison.Ordinal);

    private static bool WorkspaceExceptTabMatches(
        WorkspaceInstance before,
        WorkspaceInstance after,
        TabInstanceId changedTabId) =>
        before.Tabs.Select(tab => tab.Id).SequenceEqual(after.Tabs.Select(tab => tab.Id))
        && before.Tabs
            .Where(tab => tab.Id != changedTabId)
            .All(tab => TabEquivalent(
                tab,
                after.Tabs.SingleOrDefault(candidate => candidate.Id == tab.Id)));

    private static bool TabWithoutPanelMatches(
        TabInstance before,
        TabInstance after,
        PanelInstanceId removedPanelId) =>
        before.Id == after.Id
        && string.Equals(before.Title, after.Title, StringComparison.Ordinal)
        && SequenceEqual(
            after.Panels,
            before.Panels.Where(panel => panel.Id != removedPanelId),
            PanelEquivalent);

    private static bool TabWithAddedPanelMatches(
        TabInstance before,
        TabInstance after,
        PanelInstance moved) =>
        before.Id == after.Id
        && string.Equals(before.Title, after.Title, StringComparison.Ordinal)
        && PanelEquivalent(
            moved,
            after.Panels.SingleOrDefault(panel => panel.Id == moved.Id))
        && SequenceEqual(
            after.Panels.Where(panel => panel.Id != moved.Id),
            before.Panels,
            PanelEquivalent);

    private static bool TabEquivalent(TabInstance? left, TabInstance? right) =>
        left is not null
        && right is not null
        && left.Id == right.Id
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && left.ActivePanelId == right.ActivePanelId
        && SequenceEqual(left.Panels, right.Panels, PanelEquivalent);

    private static bool PanelEquivalent(PanelInstance? left, PanelInstance? right) =>
        left is not null
        && right is not null
        && left.Id == right.Id
        && left.Kind == right.Kind
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && left.SessionId == right.SessionId;

    private static bool SequenceEqual<T>(
        IEnumerable<T> left,
        IEnumerable<T> right,
        Func<T, T, bool> equivalent)
    {
        using var leftItems = left.GetEnumerator();
        using var rightItems = right.GetEnumerator();
        while (leftItems.MoveNext())
        {
            if (!rightItems.MoveNext()
                || !equivalent(leftItems.Current, rightItems.Current))
            {
                return false;
            }
        }

        return !rightItems.MoveNext();
    }

    private sealed record TransferGraphs(
        HostedWorkspaceGraph Source,
        HostedWorkspaceGraph Destination);

    private static bool TryReconcileSessionLinks(
        WindowInstanceId windowId,
        WorkspaceInstance workspace,
        IReadOnlyList<LiveWorkspaceSession> liveSessions,
        WorkspaceInstance? currentWorkspace,
        out WorkspaceInstance reconciled,
        out HostError? error)
    {
        var sessionsByPanel = new Dictionary<PanelInstanceId, List<SessionDescriptor>>();
        // Only the sessions claiming this workspace. It used to take everything
        // claiming this *window* too and then reject whatever did not also match
        // the workspace — which was fine while a window held one workspace, and
        // rejects every registration the moment it holds two.
        foreach (var liveSession in liveSessions.Where(session =>
                     session.Descriptor.Owner.WorkspaceId == workspace.Id))
        {
            var session = liveSession.Descriptor;
            if (session.Owner.WindowId != windowId)
            {
                reconciled = workspace;
                error = HostError.Create(
                    HostErrorCode.InvalidRequest,
                    "A live session owner does not match the registered window and workspace.");
                return false;
            }

            var tab = workspace.Tabs.SingleOrDefault(candidate =>
                candidate.Id == session.Owner.TabId);
            var panel = tab?.Panels.SingleOrDefault(candidate =>
                candidate.Id == session.Owner.PanelId);
            if (tab is null || panel is null)
            {
                reconciled = workspace;
                error = HostError.Create(
                    HostErrorCode.InvalidRequest,
                    "A live session owner does not identify a panel in the workspace graph.");
                return false;
            }

            var ownerKind = OwnerPanelKind(session.Kind, liveSession.Role);
            if (ownerKind is null || panel.Kind != ownerKind)
            {
                reconciled = workspace;
                error = HostError.Create(
                    HostErrorCode.InvalidRequest,
                    "A live session kind and role do not match its owner panel kind.");
                return false;
            }

            if (liveSession.Role == PanelSessionRole.Embedded)
            {
                continue;
            }

            if (!sessionsByPanel.TryGetValue(panel.Id, out var panelSessions))
            {
                panelSessions = [];
                sessionsByPanel.Add(panel.Id, panelSessions);
            }

            panelSessions.Add(session);
        }

        var currentPanels = currentWorkspace?.Tabs
            .SelectMany(tab => tab.Panels)
            .ToDictionary(panel => panel.Id);
        reconciled = workspace;
        foreach (var tab in workspace.Tabs)
        {
            foreach (var panel in tab.Panels)
            {
                SessionId? sessionId = null;
                sessionsByPanel.TryGetValue(panel.Id, out var panelSessions);
                if (currentPanels is not null
                    && currentPanels.TryGetValue(panel.Id, out var currentPanel))
                {
                    sessionId = currentPanel.SessionId is { } currentSessionId
                        && panelSessions?.Any(candidate => candidate.Id == currentSessionId) == true
                            ? currentSessionId
                            : null;
                }
                else if (panelSessions is not null)
                {
                    if (panelSessions.Count == 1)
                    {
                        sessionId = panelSessions[0].Id;
                    }
                    else
                    {
                        reconciled = workspace;
                        error = HostError.Create(
                            HostErrorCode.InvalidRequest,
                            "More than one live session claims the same workspace panel without an authoritative current link.");
                        return false;
                    }
                }

                reconciled = reconciled.ReplacePanelSession(tab.Id, panel.Id, sessionId);
            }
        }

        error = null;
        return true;
    }

    private static PanelKind? OwnerPanelKind(PanelKind sessionKind, PanelSessionRole role) =>
        role switch
        {
            PanelSessionRole.Primary => sessionKind,
            PanelSessionRole.Embedded when sessionKind == PanelKind.Terminal => PanelKind.Docker,
            PanelSessionRole.Embedded => null,
            _ => null,
        };

    private static HostResult<WorkspaceGraphSnapshot> InvalidSessionOwner(
        long revision,
        string message) =>
        HostResult<WorkspaceGraphSnapshot>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            revision);

    private static HostResult<WorkspaceGraphSnapshot> NotFound() =>
        HostResult<WorkspaceGraphSnapshot>.Fail(
            HostError.Create(HostErrorCode.NotFound, "The workspace graph was not found."),
            0);

    private static HostResult<T> RevisionConflict<T>(
        long currentRevision,
        long expectedRevision) =>
        HostResult<T>.Fail(
            HostError.Create(
                HostErrorCode.RevisionConflict,
                $"Expected revision {expectedRevision}, but the current revision is {currentRevision}."),
            currentRevision);
}
