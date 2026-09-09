using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class SystemMonitorRuntimePanelViewModelTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(4, 30)]
    [InlineData(12, 30)]
    public void RemotePollingBacksOffAfterRepeatedCaptureFailures(
        int consecutiveFailures,
        int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            SystemMonitorPolling.Delay(
                ConnectionKind.Ssh,
                consecutiveFailures));
    }

    [Fact]
    public void LocalPollingKeepsTheNormalCadenceAfterCaptureFailures()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            SystemMonitorPolling.Delay(
                ConnectionKind.Local,
                consecutiveFailures: 12));
    }

    [Fact]
    public async Task PanelsRemainDataFreeAndDoNotContactTheHostBeforeStart()
    {
        var (client, host) = CreateHost();
        using var statistics = CreateStatisticsPanel(client);
        using var processes = CreateProcessPanel(client);

        await statistics.RefreshAsync();
        await processes.RefreshAsync();

        Assert.True(statistics.ShowLoading);
        Assert.Null(statistics.Snapshot);
        Assert.Equal("Unavailable", statistics.CpuText);
        Assert.Equal("Unavailable", statistics.MemoryText);
        Assert.Equal("Unavailable", statistics.ProcessCountText);
        Assert.Equal("No sample captured", statistics.CapturedAtText);
        Assert.True(processes.ShowLoading);
        Assert.Null(processes.Snapshot);
        Assert.Empty(processes.Processes);
        // Nothing said twice: the cell beside it already reports that no
        // sample has been captured.
        Assert.Equal(string.Empty, processes.ShowingText);
        Assert.Equal("No sample captured", processes.CapturedAtText);
        Assert.Equal(0, host.StatisticsEnsureCount);
        Assert.Equal(0, host.ProcessEnsureCount);
        Assert.Equal(0, host.StatisticsReadCount);
        Assert.Equal(0, host.ProcessListCount);
    }

    [Fact]
    public async Task StatisticsStartEnsuresAndProjectsTheFirstHostSample()
    {
        var (client, host) = CreateHost();
        var sample = StatisticsSample();
        host.StatisticsResults.Enqueue(StatisticsSuccess(sample));
        using var panel = CreateStatisticsPanel(client);

        await panel.Start();

        Assert.Equal(1, host.StatisticsEnsureCount);
        Assert.Equal(1, host.StatisticsReadCount);
        Assert.Equal(panel.SessionId, Assert.Single(host.StatisticsRequests).SessionId);
        Assert.Equal(HostRevision, Assert.Single(host.StatisticsContexts).ExpectedRevision);
        Assert.Same(sample, panel.Snapshot);
        Assert.True(panel.HasHostedSession);
        Assert.Equal(SystemMonitorPanelState.Live, panel.State);
        Assert.Equal("37.5%", panel.CpuText);
        Assert.Equal("2.0 KiB", panel.MemoryText);
        Assert.Equal("1.5 KiB/s", panel.NetworkReceivedText);
        Assert.Equal("3.0 KiB/s", panel.NetworkSentText);
        Assert.Equal("9", panel.ProcessCountText);
        Assert.Equal("Resource details available for 7 of 9", panel.ProcessDetailText);
        Assert.Equal("1d 2h 3m", panel.UptimeText);
        Assert.Equal("8", panel.ProcessorCountText);
        Assert.Equal([37.5], panel.CpuHistory);
        Assert.Equal([2_048], panel.MemoryHistory);
        Assert.Equal([1_536], panel.NetworkReceivedHistory);
        Assert.Equal([3_072], panel.NetworkSentHistory);
    }

    [Fact]
    public async Task MonitorSessionsCarryTheSelectedConnectionSnapshot()
    {
        var (client, host) = CreateHost();
        host.StatisticsResults.Enqueue(StatisticsSuccess(StatisticsSample()));
        host.ProcessResults.Enqueue(ProcessSuccess(ProcessSample()));
        var connection = new ConnectionProfile(
            new ConnectionId("remote-monitor"),
            ConnectionProfile.CurrentSchemaVersion,
            "Remote monitor",
            new ConnectionEndpoint.Ssh("host.example", username: "operator"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        using var statistics = CreateStatisticsPanel(client, connection);
        using var processes = CreateProcessPanel(client, connection);

        await statistics.Start();
        await processes.Start();

        Assert.Equal(connection, Assert.Single(host.StatisticsRequests).Connection);
        Assert.Equal(connection, Assert.Single(host.ProcessEnsureRequests).Connection);
        Assert.Equal("Remote monitor", statistics.ConnectionDisplayName);
        Assert.Equal("Remote monitor", processes.ConnectionDisplayName);
    }

    [Fact]
    public async Task StatisticsHistoryKeepsOnlyTheLatestTwoMinuteWindow()
    {
        var (client, host) = CreateHost();
        var sampleCount = StatisticsRuntimePanelViewModel.HistoryCapacity + 3;
        for (var index = 0; index < sampleCount; index++)
        {
            host.StatisticsResults.Enqueue(StatisticsSuccess(
                StatisticsSample(
                    cpu: index,
                    memory: index * 1_024,
                    capturedAt: DateTimeOffset.UnixEpoch.AddSeconds(index * 2))));
        }

        using var panel = CreateStatisticsPanel(client);
        await panel.Start();
        for (var index = 1; index < sampleCount; index++)
        {
            await panel.RefreshAsync();
        }

        Assert.Equal(StatisticsRuntimePanelViewModel.HistoryCapacity, panel.CpuHistory.Count);
        Assert.Equal(3d, panel.CpuHistory[0]);
        Assert.Equal(sampleCount - 1d, panel.CpuHistory[^1]);
        Assert.Equal(3d * 1_024, panel.MemoryHistory[0]);
        Assert.Equal((sampleCount - 1d) * 1_024, panel.MemoryHistory[^1]);
    }

    [Fact]
    public async Task ProcessStartProjectsSortFilterAndBoundedSampleText()
    {
        var (client, host) = CreateHost();
        var snapshot = ProcessSample();
        host.ProcessResults.Enqueue(ProcessSuccess(snapshot));
        using var panel = CreateProcessPanel(client);
        panel.Sort = ProcessMonitorSort.MemoryDescending;

        await panel.Start();
        panel.Filter = "dotnet 77";

        Assert.Equal(1, host.ProcessEnsureCount);
        Assert.Equal(1, host.ProcessListCount);
        var request = Assert.Single(host.ProcessRequests);
        Assert.Equal(panel.SessionId, request.SessionId);
        Assert.Equal(ProcessMonitorSort.MemoryDescending, request.Query.Sort);
        Assert.Equal(
            ProcessMonitorQuery.DefaultMaximumResults,
            request.Query.MaximumResults);
        Assert.Equal(HostRevision, Assert.Single(host.ProcessContexts).ExpectedRevision);
        Assert.Same(snapshot, panel.Snapshot);
        Assert.Equal(SystemMonitorPanelState.Live, panel.State);
        var process = Assert.Single(panel.Processes);
        Assert.Equal(77, process.ProcessId);
        Assert.Equal("dotnet helper", process.Name);
        Assert.Equal("8.5%", process.Cpu);
        Assert.Equal("2.0 KiB", process.Memory);
        // How many are in the list, and nothing about how the sample was taken
        // — that belongs in the settings that bound it, not in every reading.
        Assert.Equal("1 process", panel.ShowingText);
    }

    [Fact]
    public void ProcessSortHeadersExposeAndToggleTheActiveDirection()
    {
        var (client, _) = CreateHost();
        using var panel = CreateProcessPanel(client);

        Assert.True(panel.IsSortingByCpu);
        Assert.True(panel.IsSortDescending);
        Assert.False(panel.IsSortingByMemory);
        Assert.False(panel.IsSortingByName);
        Assert.False(panel.IsSortingByProcessId);

        List<string?> changed = [];
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        panel.ChangeSort(ProcessMonitorSort.CpuDescending);

        Assert.Equal(ProcessMonitorSort.CpuAscending, panel.Sort);
        Assert.True(panel.IsSortingByCpu);
        Assert.False(panel.IsSortDescending);

        panel.ChangeSort(ProcessMonitorSort.CpuDescending);
        Assert.Equal(ProcessMonitorSort.CpuDescending, panel.Sort);
        Assert.True(panel.IsSortDescending);

        panel.ChangeSort(ProcessMonitorSort.NameAscending);
        Assert.Equal(ProcessMonitorSort.NameAscending, panel.Sort);
        Assert.False(panel.IsSortingByCpu);
        Assert.True(panel.IsSortingByName);
        Assert.False(panel.IsSortDescending);

        panel.ChangeSort(ProcessMonitorSort.NameAscending);
        Assert.Equal(ProcessMonitorSort.NameDescending, panel.Sort);
        Assert.True(panel.IsSortingByName);
        Assert.True(panel.IsSortDescending);
        Assert.Contains(
            nameof(ProcessMonitorRuntimePanelViewModel.IsSortingByCpu),
            changed,
            StringComparer.Ordinal);
        Assert.Contains(
            nameof(ProcessMonitorRuntimePanelViewModel.IsSortingByName),
            changed,
            StringComparer.Ordinal);
        Assert.Contains(
            nameof(ProcessMonitorRuntimePanelViewModel.IsSortDescending),
            changed,
            StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(ProcessMonitorSort.CpuDescending, ProcessMonitorSort.CpuAscending)]
    [InlineData(ProcessMonitorSort.MemoryDescending, ProcessMonitorSort.MemoryAscending)]
    [InlineData(ProcessMonitorSort.NameAscending, ProcessMonitorSort.NameDescending)]
    [InlineData(ProcessMonitorSort.ProcessIdAscending, ProcessMonitorSort.ProcessIdDescending)]
    public void EachProcessSortHeaderTogglesBothWays(
        ProcessMonitorSort defaultSort,
        ProcessMonitorSort reverseSort)
    {
        var (client, _) = CreateHost();
        using var panel = CreateProcessPanel(client);
        panel.Sort = defaultSort;

        panel.ChangeSort(defaultSort);
        Assert.Equal(reverseSort, panel.Sort);

        panel.ChangeSort(defaultSort);
        Assert.Equal(defaultSort, panel.Sort);
    }

    [Fact]
    public async Task StatisticsRecoverableFailureKeepsTheLastGoodSampleVisible()
    {
        var (client, host) = CreateHost();
        var sample = StatisticsSample();
        host.StatisticsResults.Enqueue(StatisticsSuccess(sample));
        host.StatisticsResults.Enqueue(StatisticsCaptureFailure());
        using var panel = CreateStatisticsPanel(client);
        await panel.Start();

        await panel.RefreshAsync();

        Assert.Same(sample, panel.Snapshot);
        Assert.True(panel.ShowContent);
        Assert.False(panel.ShowTerminalError);
        Assert.Equal(SystemMonitorPanelState.Stale, panel.State);
        Assert.Equal("Stale · retry available", panel.StatusText);
        Assert.Equal("Statistics refresh failed", panel.IssueTitle);
        Assert.Contains(
            "could not be captured",
            panel.IssueMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessRecoverableFailurePreservesSnapshotAndFilteredRows()
    {
        var (client, host) = CreateHost();
        var snapshot = ProcessSample();
        host.ProcessResults.Enqueue(ProcessSuccess(snapshot));
        host.ProcessResults.Enqueue(ProcessCaptureFailure());
        using var panel = CreateProcessPanel(client);
        await panel.Start();
        panel.Filter = "postgres";
        var visible = Assert.Single(panel.Processes);

        await panel.RefreshAsync();

        Assert.Same(snapshot, panel.Snapshot);
        Assert.Same(visible, Assert.Single(panel.Processes));
        Assert.Equal(SystemMonitorPanelState.Stale, panel.State);
        Assert.Equal("Process refresh failed", panel.IssueTitle);
        Assert.Equal("1 process", panel.ShowingText);
    }

    [Fact]
    public async Task ProcessRefreshRetainsRowsWithoutCollectionChurn()
    {
        var (client, host) = CreateHost();
        host.ProcessResults.Enqueue(ProcessSuccess(ProcessRange(cpuOffset: 0)));
        host.ProcessResults.Enqueue(ProcessSuccess(ProcessRange(cpuOffset: 1)));
        using var panel = CreateProcessPanel(client);
        await panel.Start();
        var originalRows = panel.Processes.ToArray();
        var collectionNotifications = 0;
        var firstRowNotifications = 0;
        panel.Processes.CollectionChanged += (_, _) => collectionNotifications++;
        originalRows[0].PropertyChanged += (_, _) => firstRowNotifications++;

        await panel.RefreshAsync();

        Assert.Equal(0, collectionNotifications);
        Assert.Equal(1, firstRowNotifications);
        Assert.Equal(ProcessMonitorQuery.DefaultMaximumResults, panel.Processes.Count);
        Assert.Equal(originalRows, panel.Processes);
        Assert.Equal("1.0%", panel.Processes[0].Cpu);
    }

    [Fact]
    public async Task DisposalCancelsAnInFlightReadWithoutLateStateMutation()
    {
        var (client, host) = CreateHost();
        host.BlockStatisticsRead = true;
        using var panel = CreateStatisticsPanel(client);

        var initialization = panel.Start();
        await host.StatisticsReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        panel.Dispose();
        await initialization;

        Assert.Equal(SystemMonitorPanelState.Disposed, panel.State);
        Assert.Equal("Monitoring stopped", panel.StatusText);
        Assert.Null(panel.Snapshot);
        Assert.False(panel.HasIssue);
        Assert.False(panel.RefreshCommand.CanExecute(null));
        Assert.Equal(1, host.StatisticsReadCount);
    }

    [Fact]
    public async Task RecoveryStoresMonitorIdentityKindTitleAndLayoutButNoSamples()
    {
        var (client, host) = CreateHost();
        host.StatisticsResults.Enqueue(StatisticsSuccess(StatisticsSample()));
        host.ProcessResults.Enqueue(ProcessSuccess(ProcessSample()));
        using var statistics = CreateStatisticsPanel(client);
        using var processes = CreateProcessPanel(client);
        await Task.WhenAll(statistics.Start(), processes.Start());
        var layout = new LayoutDefinition(
            new LayoutId("monitor-layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Monitor layout",
            new LayoutGrid(2, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("statistics"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(280, 180)),
                new LayoutSlotDefinition(
                    new LayoutSlotId("processes"),
                    new LayoutGridBounds(1, 0, 1, 1),
                    new LayoutMinimumSize(320, 200)),
            ]);
        var tab = new RuntimeTabViewModel(
            new TabInstanceId("monitor-tab"),
            "Monitor tab",
            "Test",
            layout);
        tab.AddPanel(statistics, new LayoutSlotId("statistics"));
        tab.AddPanel(processes, new LayoutSlotId("processes"));
        var workspace = new RuntimeWorkspaceViewModel(
            new WorkspaceInstanceId("monitor-workspace"),
            "Monitor workspace",
            "#123456",
            []);
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;

        var json = RuntimeWorkspaceRecoveryCodec.Serialize(workspace);
        var recovered = RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            new RuntimeRecoverySnapshot(
                "test-run",
                RuntimeWorkspaceRecoveryCodec.SnapshotKey,
                RuntimeWorkspaceRecoveryCodec.SchemaVersion,
                json,
                DateTimeOffset.UnixEpoch),
            out var payload,
            out var error);

        Assert.True(recovered, error);
        var panels = Assert.Single(payload!.Workspace!.Tabs).Panels;
        var recoveredStatistics = Assert.Single(
            panels,
            panel => panel.Kind == RuntimePanelRecoveryKind.Statistics);
        var recoveredProcesses = Assert.Single(
            panels,
            panel => panel.Kind == RuntimePanelRecoveryKind.ProcessMonitor);
        AssertMonitorRecovery(
            recoveredStatistics,
            statistics,
            column: 0,
            minimumWidth: 280,
            minimumHeight: 180);
        AssertMonitorRecovery(
            recoveredProcesses,
            processes,
            column: 1,
            minimumWidth: 320,
            minimumHeight: 200);
        Assert.DoesNotContain("sensitive-process-name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("424242", json, StringComparison.Ordinal);
        Assert.DoesNotContain("987654321", json, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processId", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertMonitorRecovery(
        RuntimePanelRecoveryPayload recovered,
        RuntimePanelViewModel source,
        int column,
        double minimumWidth,
        double minimumHeight)
    {
        Assert.Equal(source.Id.Value, recovered.Key);
        Assert.Equal(source.Title, recovered.Title);
        Assert.Equal(column, recovered.Column);
        Assert.Equal(0, recovered.Row);
        Assert.Equal(1, recovered.ColumnSpan);
        Assert.Equal(1, recovered.RowSpan);
        Assert.Equal(minimumWidth, recovered.MinimumWidth);
        Assert.Equal(minimumHeight, recovered.MinimumHeight);
        Assert.Null(recovered.KindLabel);
        Assert.Equal("builtin.local", recovered.ConnectionId);
        Assert.Null(recovered.StartupLocation);
        Assert.Null(recovered.FileProviderProfileId);
        Assert.Null(recovered.FileLocation);
        Assert.False(recovered.ShowHidden);
        Assert.Null(recovered.Filter);
    }

    private static StatisticsRuntimePanelViewModel CreateStatisticsPanel(
        ISessionHostClient sessionClient,
        ConnectionProfile? connection = null)
    {
        var id = new PanelInstanceId($"statistics-{Guid.NewGuid():N}");
        return new StatisticsRuntimePanelViewModel(
            id,
            "Statistics",
            sessionClient,
            new ClientId("monitor-client"),
            Owner(id),
            connection ?? BuiltInConnections.Local,
            ImmediateUiThreadDispatcher.Instance,
            WaitForCancellation);
    }

    private static ProcessMonitorRuntimePanelViewModel CreateProcessPanel(
        ISessionHostClient sessionClient,
        ConnectionProfile? connection = null)
    {
        var id = new PanelInstanceId($"processes-{Guid.NewGuid():N}");
        return new ProcessMonitorRuntimePanelViewModel(
            id,
            "Processes",
            sessionClient,
            new ClientId("monitor-client"),
            Owner(id),
            connection ?? BuiltInConnections.Local,
            ImmediateUiThreadDispatcher.Instance,
            WaitForCancellation);
    }

    private static SessionOwner Owner(PanelInstanceId panelId) =>
        new(
            HostMode.Desktop,
            new WindowInstanceId("monitor-window"),
            new WorkspaceInstanceId("monitor-workspace"),
            new TabInstanceId("monitor-tab"),
            panelId);

    private static Task WaitForCancellation(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        _ = delay;
        return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static SystemStatisticsSnapshot StatisticsSample(
        double? cpu = 37.5,
        long memory = 2_048,
        DateTimeOffset? capturedAt = null) =>
        new(
            capturedAt ?? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new TimeSpan(1, 2, 3, 4),
            8,
            9,
            7,
            cpu,
            memory,
            1_536,
            3_072);

    private static ProcessMonitorSnapshot ProcessSample() =>
        new(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            [
                new ProcessMonitorEntry(
                    424242,
                    "sensitive-process-name",
                    2.5,
                    987654321,
                    TimeSpan.FromSeconds(50),
                    DateTimeOffset.UnixEpoch,
                    false),
                new ProcessMonitorEntry(
                    12,
                    "postgres",
                    null,
                    1_024,
                    null,
                    null,
                    false),
                new ProcessMonitorEntry(
                    77,
                    "dotnet helper",
                    8.5,
                    2_048,
                    TimeSpan.FromSeconds(4),
                    DateTimeOffset.UnixEpoch.AddMinutes(1),
                    true),
            ],
            9_000,
            3,
            true);

    private static ProcessMonitorSnapshot ProcessRange(int cpuOffset) =>
        new(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5 + cpuOffset, TimeSpan.Zero),
            [.. Enumerable.Range(1, ProcessMonitorQuery.DefaultMaximumResults)
                .Select(index => new ProcessMonitorEntry(
                    index,
                    $"process-{index}",
                    (index - 1 + cpuOffset) % 100,
                    index * 1_024,
                    TimeSpan.FromSeconds(index),
                    DateTimeOffset.UnixEpoch.AddSeconds(index),
                    false))],
            ProcessMonitorQuery.DefaultMaximumResults,
            ProcessMonitorQuery.DefaultMaximumResults,
            false);

    private static HostResult<MonitorPanelResult<SystemStatisticsSnapshot>>
        StatisticsSuccess(SystemStatisticsSnapshot snapshot) =>
        HostResult<MonitorPanelResult<SystemStatisticsSnapshot>>.Succeed(
            MonitorPanelResult<SystemStatisticsSnapshot>.Success(snapshot),
            HostRevision);

    private static HostResult<MonitorPanelResult<SystemStatisticsSnapshot>>
        StatisticsCaptureFailure() =>
        HostResult<MonitorPanelResult<SystemStatisticsSnapshot>>.Succeed(
            MonitorPanelResult<SystemStatisticsSnapshot>.Failure(
                MonitorPanelError.Create(MonitorPanelErrorCode.CaptureFailed)),
            HostRevision);

    private static HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>
        ProcessSuccess(ProcessMonitorSnapshot snapshot) =>
        HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>.Succeed(
            MonitorPanelResult<ProcessMonitorSnapshot>.Success(snapshot),
            HostRevision);

    private static HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>
        ProcessCaptureFailure() =>
        HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>.Succeed(
            MonitorPanelResult<ProcessMonitorSnapshot>.Failure(
                MonitorPanelError.Create(MonitorPanelErrorCode.CaptureFailed)),
            HostRevision);

    private static (ISessionHostClient Client, RecordingMonitorHost Proxy) CreateHost()
    {
        var client = DispatchProxy.Create<ISessionHostClient, RecordingMonitorHost>();
        return (client, (RecordingMonitorHost)(object)client);
    }

    private const long HostRevision = 11;

    private sealed class ImmediateUiThreadDispatcher : IUiThreadDispatcher
    {
        public static ImmediateUiThreadDispatcher Instance { get; } = new();

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    public class RecordingMonitorHost : DispatchProxy
    {
        public Queue<HostResult<MonitorPanelResult<SystemStatisticsSnapshot>>>
            StatisticsResults
        { get; } = [];

        public Queue<HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>>
            ProcessResults
        { get; } = [];

        public List<EnsureStatisticsSessionRequest> StatisticsRequests { get; } = [];

        public List<EnsureProcessMonitorSessionRequest> ProcessEnsureRequests { get; } = [];

        public List<OperationContext> StatisticsContexts { get; } = [];

        public List<OperationContext> ProcessContexts { get; } = [];

        public List<ProcessMonitorHostRequest> ProcessRequests { get; } = [];

        public TaskCompletionSource StatisticsReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockStatisticsRead { get; set; }

        public int StatisticsEnsureCount => StatisticsRequests.Count;

        public int ProcessEnsureCount => ProcessEnsureRequests.Count;

        public int StatisticsReadCount => StatisticsContexts.Count;

        public int ProcessListCount => ProcessContexts.Count;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(ISessionHostClient.EnsureStatisticsSessionAsync)
                    when args is
                    [
                        EnsureStatisticsSessionRequest request,
                        OperationContext context,
                        CancellationToken cancellationToken,
                    ] =>
                    EnsureStatisticsAsync(request, context, cancellationToken),
                nameof(ISessionHostClient.EnsureProcessMonitorSessionAsync)
                    when args is
                    [
                        EnsureProcessMonitorSessionRequest request,
                        OperationContext context,
                        CancellationToken cancellationToken,
                    ] =>
                    EnsureProcessesAsync(request, context, cancellationToken),
                nameof(ISessionHostClient.ReadStatisticsAsync)
                    when args is
                    [
                        SessionId sessionId,
                        OperationContext context,
                        CancellationToken cancellationToken,
                    ] =>
                    ReadStatisticsAsync(sessionId, context, cancellationToken),
                nameof(ISessionHostClient.ListProcessesAsync)
                    when args is
                    [
                        ProcessMonitorHostRequest request,
                        OperationContext context,
                        CancellationToken cancellationToken,
                    ] =>
                    ListProcessesAsync(request, context, cancellationToken),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private ValueTask<HostResult<SessionSnapshot>> EnsureStatisticsAsync(
            EnsureStatisticsSessionRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatisticsRequests.Add(request);
            Assert.NotNull(context.IdempotencyKey);
            return ValueTask.FromResult(SessionSuccess(
                request.SessionId,
                request.Owner,
                PanelKind.Statistics,
                SessionCapabilities.StatisticsRead));
        }

        private ValueTask<HostResult<SessionSnapshot>> EnsureProcessesAsync(
            EnsureProcessMonitorSessionRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessEnsureRequests.Add(request);
            Assert.NotNull(context.IdempotencyKey);
            return ValueTask.FromResult(SessionSuccess(
                request.SessionId,
                request.Owner,
                PanelKind.ProcessMonitor,
                SessionCapabilities.ProcessesList));
        }

        private async ValueTask<
            HostResult<MonitorPanelResult<SystemStatisticsSnapshot>>> ReadStatisticsAsync(
            SessionId sessionId,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Assert.Single(StatisticsRequests).SessionId, sessionId);
            StatisticsContexts.Add(context);
            StatisticsReadStarted.TrySetResult();
            if (BlockStatisticsRead)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return StatisticsResults.Count > 0
                ? StatisticsResults.Dequeue()
                : throw new InvalidOperationException(
                    "No statistics response was queued.");
        }

        private ValueTask<HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>>
            ListProcessesAsync(
                ProcessMonitorHostRequest request,
                OperationContext context,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Assert.Single(ProcessEnsureRequests).SessionId, request.SessionId);
            ProcessRequests.Add(request);
            ProcessContexts.Add(context);
            return ValueTask.FromResult(ProcessResults.Count > 0
                ? ProcessResults.Dequeue()
                : throw new InvalidOperationException(
                    "No process-monitor response was queued."));
        }

        private static HostResult<SessionSnapshot> SessionSuccess(
            SessionId sessionId,
            SessionOwner owner,
            PanelKind kind,
            string capability)
        {
            var descriptor = new SessionDescriptor(
                sessionId,
                kind,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                owner,
                new CapabilitySet([SessionCapabilities.AttachRead, capability]),
                HostRevision,
                false,
                "Ready");
            return HostResult<SessionSnapshot>.Succeed(
                new SessionSnapshot(descriptor, 1, [], null),
                HostRevision);
        }
    }
}
