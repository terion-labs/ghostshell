using System.Runtime.CompilerServices;
using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

internal sealed class FakeSystemMonitorPanelSessionFactory
    : ISystemMonitorPanelSessionFactory
{
    private readonly Dictionary<SessionId, FakeProcessMonitorPanelSession> _processes = [];
    private readonly Dictionary<SessionId, FakeStatisticsPanelSession> _statistics = [];

    public CapabilitySet StatisticsCapabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.StatisticsRead,
    ]);

    public CapabilitySet ProcessMonitorCapabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.ProcessesList,
    ]);

    public int StatisticsCreateCount { get; private set; }

    public int ProcessMonitorCreateCount { get; private set; }

    public List<WorkspaceInstanceId> StatisticsWorkspaceIds { get; } = [];

    public List<WorkspaceInstanceId> ProcessMonitorWorkspaceIds { get; } = [];

    public Func<FakeMonitorPanelSession, CancellationToken, ValueTask>? AfterCreateAsync
    {
        get;
        set;
    }

    public Func<CancellationToken, ValueTask>? BeforeSnapshotForNewSessions
    {
        get;
        set;
    }

    public FakeStatisticsPanelSession Statistics(SessionId id) => _statistics[id];

    public FakeProcessMonitorPanelSession Processes(SessionId id) => _processes[id];

    public async ValueTask<IStatisticsPanelSession> CreateStatisticsAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatisticsCreateCount++;
        StatisticsWorkspaceIds.Add(workspaceId);
        var session = new FakeStatisticsPanelSession(
            sessionId,
            StatisticsCapabilities)
        {
            BeforeSnapshotAsync = BeforeSnapshotForNewSessions,
        };
        _statistics.Add(sessionId, session);
        if (AfterCreateAsync is { } afterCreate)
        {
            await afterCreate(session, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    public async ValueTask<IProcessMonitorPanelSession> CreateProcessMonitorAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessMonitorCreateCount++;
        ProcessMonitorWorkspaceIds.Add(workspaceId);
        var session = new FakeProcessMonitorPanelSession(
            sessionId,
            ProcessMonitorCapabilities)
        {
            BeforeSnapshotAsync = BeforeSnapshotForNewSessions,
        };
        _processes.Add(sessionId, session);
        if (AfterCreateAsync is { } afterCreate)
        {
            await afterCreate(session, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }
}

internal abstract class FakeMonitorPanelSession(
    SessionId id,
    PanelKind kind,
    CapabilitySet capabilities) : IPanelSession
{
    private CapabilitySet _capabilities = capabilities;
    private bool _closed;

    public SessionId Id { get; } = id;

    public PanelKind Kind { get; } = kind;

    public CapabilitySet Capabilities => _capabilities;

    public int CloseCount { get; private set; }

    public int DisposeCount { get; private set; }

    public Func<CancellationToken, ValueTask>? BeforeSnapshotAsync { get; set; }

    public bool IsClosed => _closed;

    public PanelCloseMode? LastCloseMode { get; private set; }

    public void RemoveCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        _capabilities = new CapabilitySet(
            _capabilities.Values.Where(
                value => !string.Equals(
                    value,
                    capability,
                    StringComparison.Ordinal)));
    }

    public async ValueTask<PanelSessionSnapshot> SnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (BeforeSnapshotAsync is { } beforeSnapshot)
        {
            await beforeSnapshot(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _closed
            ? new PanelSessionSnapshot(
                SessionLifecycle.Closed,
                SessionHealth.Ended,
                false,
                "Closed")
            : new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                false,
                "Ready");
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = afterSequence;
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseCount++;
        LastCloseMode = mode;
        if (_closed)
        {
            return ValueTask.FromResult(PanelCloseOutcome.AlreadyClosed);
        }

        _closed = true;
        return ValueTask.FromResult(mode == PanelCloseMode.Force
            ? PanelCloseOutcome.ForceTerminated
            : PanelCloseOutcome.GracefullyClosed);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        _closed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeStatisticsPanelSession(
    SessionId id,
    CapabilitySet capabilities)
    : FakeMonitorPanelSession(id, PanelKind.Statistics, capabilities),
      IStatisticsPanelSession
{
    public int ReadCount { get; private set; }

    public bool BlockRead { get; set; }

    public TaskCompletionSource ReadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseRead { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MonitorPanelError? ReadError { get; set; }

    public SystemStatisticsSnapshot Snapshot { get; set; } =
        new(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromHours(1),
            4,
            8,
            7,
            12.5,
            4_096);

    public async ValueTask<MonitorPanelResult<SystemStatisticsSnapshot>> ReadStatisticsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        ReadStarted.TrySetResult();
        if (BlockRead)
        {
            await ReleaseRead.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return ReadError is { } error
            ? MonitorPanelResult<SystemStatisticsSnapshot>.Failure(error)
            : MonitorPanelResult<SystemStatisticsSnapshot>.Success(Snapshot);
    }
}

internal sealed class FakeProcessMonitorPanelSession(
    SessionId id,
    CapabilitySet capabilities)
    : FakeMonitorPanelSession(id, PanelKind.ProcessMonitor, capabilities),
      IProcessMonitorPanelSession
{
    public int ListCount { get; private set; }

    public bool BlockList { get; set; }

    public TaskCompletionSource ListStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseList { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MonitorPanelError? ListError { get; set; }

    public ProcessMonitorQuery? LastQuery { get; private set; }

    public ProcessMonitorSnapshot Snapshot { get; set; } =
        new(
            DateTimeOffset.UnixEpoch,
            [],
            0,
            0,
            false);

    public async ValueTask<MonitorPanelResult<ProcessMonitorSnapshot>> ListProcessesAsync(
        ProcessMonitorQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListCount++;
        LastQuery = query;
        ListStarted.TrySetResult();
        if (BlockList)
        {
            await ReleaseList.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (ListError is { } error)
        {
            return MonitorPanelResult<ProcessMonitorSnapshot>.Failure(error);
        }

        return MonitorPanelResult<ProcessMonitorSnapshot>.Success(
            Snapshot);
    }
}
