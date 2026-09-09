using System.Runtime.CompilerServices;
using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

internal sealed class HostedWorkspaceGraph
{
    private readonly object _gate = new();
    private readonly List<WorkspaceGraphEvent> _events = [];
    private readonly int _eventRetention;
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _changed = NewSignal();
    private WorkspaceInstance _workspace;
    private bool _removed;
    private long _revision;
    private long _sequence;

    public HostedWorkspaceGraph(
        WindowInstanceId windowId,
        WorkspaceInstance workspace,
        int eventRetention,
        TimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(windowId.Value))
        {
            throw new ArgumentException("A runtime identifier is required.", nameof(windowId));
        }

        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (eventRetention < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(eventRetention));
        }

        WindowId = windowId;
        _workspace = new WorkspaceInstance(workspace);
        _eventRetention = eventRetention;
        _timeProvider = timeProvider;
        AppendEventUnsafe(WorkspaceGraphEventKind.Registered);
    }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstanceId WorkspaceId
    {
        get
        {
            lock (_gate)
            {
                return _workspace.Id;
            }
        }
    }

    public long Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    public WorkspaceGraphSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotUnsafe();
        }
    }

    public HostResult<WorkspaceGraphSnapshot> Replace(
        WorkspaceInstance workspace,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (_gate)
        {
            if (TryRejectMutationUnsafe(expectedRevision, out var failure))
            {
                return failure;
            }

            if (workspace.Id != _workspace.Id)
            {
                return HostResult<WorkspaceGraphSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.InvalidRequest,
                        "A workspace graph replacement must preserve the workspace instance ID."),
                    _revision);
            }

            _workspace = new WorkspaceInstance(workspace);
            AppendEventUnsafe(WorkspaceGraphEventKind.Replaced);
            return SuccessUnsafe();
        }
    }

    public HostResult<WorkspaceGraphSnapshot> ActivateTab(
        TabInstanceId tabId,
        long? expectedRevision)
    {
        lock (_gate)
        {
            if (TryRejectMutationUnsafe(expectedRevision, out var failure))
            {
                return failure;
            }

            if (_workspace.Tabs.All(tab => tab.Id != tabId))
            {
                return NotFoundUnsafe("tab");
            }

            var activated = _workspace.ActivateTab(tabId);
            if (ReferenceEquals(activated, _workspace))
            {
                return SuccessUnsafe();
            }

            _workspace = activated;
            AppendEventUnsafe(WorkspaceGraphEventKind.TabActivated, tabId);
            return SuccessUnsafe();
        }
    }

    public HostResult<WorkspaceGraphSnapshot> ActivatePanel(
        TabInstanceId tabId,
        PanelInstanceId panelId,
        long? expectedRevision)
    {
        lock (_gate)
        {
            if (TryRejectMutationUnsafe(expectedRevision, out var failure))
            {
                return failure;
            }

            var tab = _workspace.Tabs.SingleOrDefault(candidate => candidate.Id == tabId);
            if (tab is null)
            {
                return NotFoundUnsafe("tab");
            }

            if (tab.Panels.All(panel => panel.Id != panelId))
            {
                return NotFoundUnsafe("panel");
            }

            var activated = _workspace.ActivatePanel(tabId, panelId);
            if (ReferenceEquals(activated, _workspace))
            {
                return SuccessUnsafe();
            }

            _workspace = activated;
            AppendEventUnsafe(WorkspaceGraphEventKind.PanelActivated, tabId, panelId);
            return SuccessUnsafe();
        }
    }

    public WorkspaceGraphSnapshot CommitTransfer(
        WorkspaceInstance workspace,
        TabInstanceId tabId,
        PanelInstanceId? panelId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (_gate)
        {
            if (_removed)
            {
                throw new InvalidOperationException(
                    "A removed workspace graph cannot accept a transfer commit.");
            }

            if (workspace.Id != _workspace.Id)
            {
                throw new InvalidOperationException(
                    "A transfer commit must preserve the workspace identity.");
            }

            _workspace = new WorkspaceInstance(workspace);
            AppendEventUnsafe(
                WorkspaceGraphEventKind.TransferCommitted,
                tabId,
                panelId);
            return SnapshotUnsafe();
        }
    }

    public HostResult<WorkspaceGraphSnapshot> ValidateSessionOwner(
        TabInstanceId tabId,
        PanelInstanceId panelId,
        PanelKind kind)
    {
        lock (_gate)
        {
            if (_removed)
            {
                return NotFoundUnsafe("workspace graph");
            }

            return ValidateOwnedPanelUnsafe(tabId, panelId, kind)
                ?? SuccessUnsafe();
        }
    }

    public HostResult<WorkspaceGraphSnapshot> LinkSession(
        TabInstanceId tabId,
        PanelInstanceId panelId,
        PanelKind kind,
        SessionId sessionId)
    {
        lock (_gate)
        {
            if (_removed)
            {
                return NotFoundUnsafe("workspace graph");
            }

            var invalid = ValidateOwnedPanelUnsafe(tabId, panelId, kind);
            if (invalid is not null)
            {
                return invalid;
            }

            var panel = _workspace.Tabs
                .Single(tab => tab.Id == tabId)
                .Panels
                .Single(candidate => candidate.Id == panelId);
            if (panel.SessionId == sessionId)
            {
                return SuccessUnsafe();
            }

            _workspace = _workspace.ReplacePanelSession(tabId, panelId, sessionId);
            AppendEventUnsafe(
                WorkspaceGraphEventKind.PanelSessionLinked,
                tabId,
                panelId,
                sessionId);
            return SuccessUnsafe();
        }
    }

    public void UnlinkSession(
        TabInstanceId tabId,
        PanelInstanceId panelId,
        PanelKind kind,
        SessionId sessionId)
    {
        lock (_gate)
        {
            if (_removed
                || ValidateOwnedPanelUnsafe(tabId, panelId, kind) is not null)
            {
                return;
            }

            var panel = _workspace.Tabs
                .Single(tab => tab.Id == tabId)
                .Panels
                .Single(candidate => candidate.Id == panelId);
            if (panel.SessionId != sessionId)
            {
                return;
            }

            _workspace = _workspace.ReplacePanelSession(tabId, panelId, null);
            AppendEventUnsafe(
                WorkspaceGraphEventKind.PanelSessionUnlinked,
                tabId,
                panelId,
                sessionId);
        }
    }

    public long Remove()
    {
        lock (_gate)
        {
            if (_removed)
            {
                return _revision;
            }

            _removed = true;
            AppendEventUnsafe(WorkspaceGraphEventKind.Removed);
            return _revision;
        }
    }

    public async IAsyncEnumerable<WorkspaceGraphStreamItem> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            WorkspaceGraphEvent[] pending;
            WorkspaceGraphSnapshot? resynchronizationSnapshot = null;
            Task waitTask;
            var complete = false;
            lock (_gate)
            {
                if (_removed)
                {
                    var removal = _events[^1];
                    pending = afterSequence < removal.Sequence ? [removal] : [];
                    waitTask = Task.CompletedTask;
                    complete = true;
                }
                else
                {
                    var oldestSequence = _events.Count == 0
                        ? _sequence + 1
                        : _events[0].Sequence;
                    if (afterSequence < oldestSequence - 1)
                    {
                        resynchronizationSnapshot = SnapshotUnsafe();
                        pending = [];
                        waitTask = Task.CompletedTask;
                    }
                    else
                    {
                        pending = [.. _events.Where(item => item.Sequence > afterSequence)];
                        waitTask = _changed.Task;
                    }
                }
            }

            if (resynchronizationSnapshot is not null)
            {
                yield return new WorkspaceGraphStreamItem.ResynchronizationRequired(
                    resynchronizationSnapshot,
                    resynchronizationSnapshot.LastSequence);
                yield break;
            }

            foreach (var workspaceEvent in pending)
            {
                yield return new WorkspaceGraphStreamItem.Event(workspaceEvent);
                afterSequence = workspaceEvent.Sequence;
            }

            if (complete)
            {
                yield break;
            }

            if (pending.Length > 0)
            {
                continue;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryRejectMutationUnsafe(
        long? expectedRevision,
        out HostResult<WorkspaceGraphSnapshot> failure)
    {
        if (_removed)
        {
            failure = HostResult<WorkspaceGraphSnapshot>.Fail(
                HostError.Create(HostErrorCode.NotFound, "The workspace graph was removed."),
                _revision);
            return true;
        }

        if (expectedRevision is { } expected && expected != _revision)
        {
            failure = RevisionConflict(_revision, expected);
            return true;
        }

        failure = null!;
        return false;
    }

    private HostResult<WorkspaceGraphSnapshot> SuccessUnsafe()
    {
        var snapshot = SnapshotUnsafe();
        return HostResult<WorkspaceGraphSnapshot>.Succeed(snapshot, snapshot.Revision);
    }

    private HostResult<WorkspaceGraphSnapshot> NotFoundUnsafe(string resource) =>
        HostResult<WorkspaceGraphSnapshot>.Fail(
            HostError.Create(HostErrorCode.NotFound, $"The {resource} was not found in the workspace graph."),
            _revision);

    private HostResult<WorkspaceGraphSnapshot>? ValidateOwnedPanelUnsafe(
        TabInstanceId tabId,
        PanelInstanceId panelId,
        PanelKind kind)
    {
        var tab = _workspace.Tabs.SingleOrDefault(candidate => candidate.Id == tabId);
        if (tab is null)
        {
            return InvalidOwnerUnsafe("The session owner tab does not belong to the workspace graph.");
        }

        var panel = tab.Panels.SingleOrDefault(candidate => candidate.Id == panelId);
        if (panel is null)
        {
            return InvalidOwnerUnsafe("The session owner panel does not belong to the owner tab.");
        }

        return panel.Kind != kind
            ? InvalidOwnerUnsafe("The session kind does not match the owner panel kind.")
            : null;
    }

    private HostResult<WorkspaceGraphSnapshot> InvalidOwnerUnsafe(string message) =>
        HostResult<WorkspaceGraphSnapshot>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            _revision);

    private WorkspaceGraphSnapshot SnapshotUnsafe() =>
        new(WindowId, _workspace, _revision, _sequence);

    private void AppendEventUnsafe(
        WorkspaceGraphEventKind kind,
        TabInstanceId? tabId = null,
        PanelInstanceId? panelId = null,
        SessionId? sessionId = null)
    {
        _revision++;
        _sequence++;
        _events.Add(new WorkspaceGraphEvent(
            WindowId,
            _workspace,
            _sequence,
            _revision,
            kind,
            _timeProvider.GetUtcNow(),
            tabId,
            panelId,
            sessionId));
        if (_events.Count > _eventRetention)
        {
            _events.RemoveRange(0, _events.Count - _eventRetention);
        }

        var changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
    }

    private static HostResult<WorkspaceGraphSnapshot> RevisionConflict(
        long currentRevision,
        long expectedRevision) =>
        HostResult<WorkspaceGraphSnapshot>.Fail(
            HostError.Create(
                HostErrorCode.RevisionConflict,
                $"Expected revision {expectedRevision}, but the current revision is {currentRevision}."),
            currentRevision);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
