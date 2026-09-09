using Asura.Application;
using Asura.Core;

namespace Asura.Monitoring.Tests;

public sealed class MonitorPanelSessionTests
{
    [Fact]
    public async Task ProcessQuerySortsAndReportsResultLimitTruncation()
    {
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(
            Process(7, "small", 10),
            Process(2, "largest", 1_000),
            Process(5, "middle", 100)));
        await using var session = ProcessSession(source);

        var result = await session.ListProcessesAsync(
            new ProcessMonitorQuery(2, ProcessMonitorSort.MemoryDescending),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal([2, 5], result.Value!.Processes.Select(process => process.ProcessId));
        Assert.True(result.Value.IsTruncated);
        Assert.Equal(3, result.Value.EnumeratedProcessCount);
        Assert.Equal(3, result.Value.ObservedProcessCount);
        Assert.Equal(3, result.Value.MatchingProcessCount);
    }

    [Theory]
    [InlineData(ProcessMonitorSort.MemoryAscending, 2, 7, 5)]
    [InlineData(ProcessMonitorSort.NameDescending, 5, 2, 7)]
    [InlineData(ProcessMonitorSort.ProcessIdDescending, 7, 5, 2)]
    public async Task ProcessQuerySupportsReverseSortOrders(
        ProcessMonitorSort sort,
        int first,
        int second,
        int third)
    {
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(
            Process(7, "alpha", 100),
            Process(2, "middle", 10),
            Process(5, "zulu", 1_000)));
        await using var session = ProcessSession(source);

        var result = await session.ListProcessesAsync(
            new ProcessMonitorQuery(3, sort),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            [first, second, third],
            result.Value!.Processes.Select(process => process.ProcessId));
    }

    [Fact]
    public async Task ProcessQuerySortsAscendingCpuBeforeApplyingTheLimit()
    {
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(
            Process(1, "lower", 10, TimeSpan.FromSeconds(1)),
            Process(2, "higher", 20, TimeSpan.FromSeconds(1))));
        source.Enqueue(Capture(
            Process(1, "lower", 10, TimeSpan.FromSeconds(2)),
            Process(2, "higher", 20, TimeSpan.FromSeconds(4))));
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var session = ProcessSession(source, clock);

        _ = await session.ListProcessesAsync(
            new ProcessMonitorQuery(2, ProcessMonitorSort.ProcessIdAscending),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await session.ListProcessesAsync(
            new ProcessMonitorQuery(1, ProcessMonitorSort.CpuAscending),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            [1],
            result.Value!.Processes.Select(process => process.ProcessId));
        Assert.True(result.Value.IsTruncated);
    }

    [Fact]
    public async Task ProcessQueryFiltersAndPagesBeforeApplyingTheLimit()
    {
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(
            Process(1, "dotnet-alpha", 10),
            Process(2, "other", 20),
            Process(3, "dotnet-charlie", 30),
            Process(4, "dotnet-delta", 40)));
        await using var session = ProcessSession(source);

        var result = await session.ListProcessesAsync(
            new ProcessMonitorQuery(
                MaximumResults: 1,
                Sort: ProcessMonitorSort.ProcessIdAscending,
                Offset: 1,
                NameContains: "DOTNET"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal([3], result.Value!.Processes.Select(process => process.ProcessId));
        Assert.Equal(4, result.Value.EnumeratedProcessCount);
        Assert.Equal(4, result.Value.ObservedProcessCount);
        Assert.Equal(3, result.Value.MatchingProcessCount);
        Assert.True(result.Value.IsTruncated);
    }

    [Theory]
    [InlineData(0, ProcessMonitorSort.CpuDescending)]
    [InlineData(ProcessMonitorQuery.MaximumAllowedResults + 1, ProcessMonitorSort.CpuDescending)]
    [InlineData(1, (ProcessMonitorSort)999)]
    public async Task ProcessQueryRejectsInvalidBoundsAndSorts(
        int maximumResults,
        ProcessMonitorSort sort)
    {
        var source = new SequenceProcessSnapshotSource();
        await using var session = ProcessSession(source);

        var result = await session.ListProcessesAsync(
            new ProcessMonitorQuery(maximumResults, sort),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(MonitorPanelErrorCode.InvalidQuery, result.Error!.Code);
        Assert.Equal("monitor_invalid_query", result.Error.StableCode);
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task SourceTruncationIsPreservedWhenQueryDoesNotTruncate()
    {
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(new RawProcessCapture(
            TimeSpan.FromMinutes(2),
            7_000,
            Array.AsReadOnly([Process(1, "visible", 10)]),
            true));
        await using var session = ProcessSession(source);

        var result = await session.ListProcessesAsync(
            new ProcessMonitorQuery(10, ProcessMonitorSort.ProcessIdAscending),
            CancellationToken.None);

        Assert.True(result.Value!.IsTruncated);
        Assert.Equal(7_000, result.Value.EnumeratedProcessCount);
    }

    [Fact]
    public async Task ClosePublishesTerminalEventAndRejectsFurtherReads()
    {
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(Process(1, "unused", 10)));
        await using var session = ProcessSession(source);
        await using var events = session
            .WatchAsync(0, CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        Assert.Equal(1, events.Current.Sequence);
        Assert.Equal(SessionLifecycle.Active, events.Current.Lifecycle);
        Assert.Equal(SessionHealth.Healthy, events.Current.Health);

        var closed = await session.CloseAsync(
            PanelCloseMode.Graceful,
            CancellationToken.None);

        Assert.Equal(PanelCloseOutcome.GracefullyClosed, closed);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(2, events.Current.Sequence);
        Assert.Equal(SessionLifecycle.Closed, events.Current.Lifecycle);
        Assert.Equal(SessionHealth.Ended, events.Current.Health);
        Assert.False(await events.MoveNextAsync());
        Assert.Equal(
            PanelCloseOutcome.AlreadyClosed,
            await session.CloseAsync(PanelCloseMode.Force, CancellationToken.None));

        var snapshot = await session.SnapshotAsync(CancellationToken.None);
        Assert.Equal(SessionLifecycle.Closed, snapshot.Lifecycle);
        Assert.False(snapshot.HasActiveWork);

        var read = await session.ListProcessesAsync(
            new ProcessMonitorQuery(),
            CancellationToken.None);
        Assert.False(read.IsSuccess);
        Assert.Equal(MonitorPanelErrorCode.SessionClosed, read.Error!.Code);
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task CloseCancelsAnActiveProcessCapture()
    {
        var captureStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(cancellationToken =>
        {
            captureStarted.TrySetResult();
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            return Capture(Process(1, "unreachable", 10));
        });
        await using var session = ProcessSession(source);

        var listing = Task.Run(async () => await session.ListProcessesAsync(
            new ProcessMonitorQuery(),
            CancellationToken.None));
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var close = await session.CloseAsync(
            PanelCloseMode.Graceful,
            CancellationToken.None);
        var result = await listing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PanelCloseOutcome.GracefullyClosed, close);
        Assert.False(result.IsSuccess);
        Assert.Equal(MonitorPanelErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(1, source.CaptureCount);
    }

    [Fact]
    public async Task FactoryCreatesLeastPrivilegeSessionsOverOneConnectionSampler()
    {
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(Process(1, "statistics", 10)));
        source.Enqueue(Capture(Process(2, "processes", 20)));
        var factory = new SystemMonitorPanelSessionFactory(
            source,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        await using var statistics = await factory.CreateStatisticsAsync(
            new SessionId("statistics-1"),
            CancellationToken.None);
        await using var processes = await factory.CreateProcessMonitorAsync(
            new SessionId("processes-1"),
            CancellationToken.None);

        Assert.True(statistics.Capabilities.Contains(SessionCapabilities.AttachRead));
        Assert.True(statistics.Capabilities.Contains(SessionCapabilities.StatisticsRead));
        Assert.False(statistics.Capabilities.Contains(SessionCapabilities.ProcessesList));
        Assert.True(processes.Capabilities.Contains(SessionCapabilities.AttachRead));
        Assert.True(processes.Capabilities.Contains(SessionCapabilities.ProcessesList));
        Assert.False(processes.Capabilities.Contains(SessionCapabilities.StatisticsRead));
        Assert.True((await statistics.ReadStatisticsAsync(CancellationToken.None)).IsSuccess);
        Assert.True((await processes.ListProcessesAsync(
            new ProcessMonitorQuery(),
            CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task EquivalentConnectionSnapshotsShareOneCapturePipeline()
    {
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(Process(1, "shared", 10)));
        var factory = new SystemMonitorPanelSessionFactory(
            source,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var firstConnection = BuiltInConnections.Local;
        var secondConnection = new ConnectionProfile(
            firstConnection.Id,
            firstConnection.SchemaVersion,
            firstConnection.Name,
            firstConnection.Endpoint,
            firstConnection.Authentication,
            firstConnection.Startup,
            firstConnection.KeepAlive,
            firstConnection.HostKeyPolicy,
            [.. firstConnection.Tags]);

        await using var statistics = await factory.CreateStatisticsAsync(
            new SessionId("statistics-equivalent"),
            firstConnection,
            CancellationToken.None);
        await using var processes = await factory.CreateProcessMonitorAsync(
            new SessionId("processes-equivalent"),
            secondConnection,
            CancellationToken.None);

        Assert.True((await statistics.ReadStatisticsAsync(CancellationToken.None)).IsSuccess);
        Assert.True((await processes.ListProcessesAsync(
            new ProcessMonitorQuery(),
            CancellationToken.None)).IsSuccess);
        Assert.Equal(1, source.CaptureCount);
    }

    private static ProcessMonitorPanelSession ProcessSession(
        IProcessSnapshotSource source,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        return new ProcessMonitorPanelSession(
            new SessionId("process-monitor-1"),
            new ProcessResourceSampler(
                source,
                clock),
            new CapabilitySet(
            [
                SessionCapabilities.AttachRead,
                SessionCapabilities.ProcessesList,
            ]),
            clock);
    }

    private static RawProcessCapture Capture(params RawProcessObservation[] processes) =>
        new(
            TimeSpan.FromMinutes(2),
            processes.Length,
            Array.AsReadOnly(processes),
            false);

    private static RawProcessObservation Process(
        int processId,
        string name,
        long workingSetBytes) =>
        Process(
            processId,
            name,
            workingSetBytes,
            TimeSpan.FromSeconds(processId));

    private static RawProcessObservation Process(
        int processId,
        string name,
        long workingSetBytes,
        TimeSpan totalProcessorTime) =>
        new(
            processId,
            name,
            workingSetBytes,
            totalProcessorTime,
            DateTimeOffset.UnixEpoch.AddSeconds(processId),
            false);
}
