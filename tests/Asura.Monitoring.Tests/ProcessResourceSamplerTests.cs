using Asura.Application;

namespace Asura.Monitoring.Tests;

public sealed class ProcessResourceSamplerTests
{
    private static readonly DateTimeOffset FirstStart =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task FirstSampleDoesNotInventCpuPercentages()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(
            Process(41, "asura", 128, TimeSpan.FromSeconds(2), FirstStart, true)));
        var sampler = new ProcessResourceSampler(source, clock);

        var result = await sampler.CaptureAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var sample = Assert.IsType<ProcessResourceSample>(result.Value);
        Assert.Null(Assert.Single(sample.Processes).CpuPercent);
        Assert.Null(sample.Statistics.ObservedCpuPercent);
        Assert.Equal(128, sample.Statistics.ObservedWorkingSetBytes);
    }

    [Fact]
    public async Task SecondSampleUsesElapsedAndProcessorTimeDeltas()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(
            Process(41, "asura", 128, TimeSpan.FromSeconds(2), FirstStart, true)));
        source.Enqueue(Capture(
            Process(41, "asura", 256, TimeSpan.FromSeconds(2.5), FirstStart, true)));
        var sampler = new ProcessResourceSampler(source, clock);

        _ = await sampler.CaptureAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await sampler.CaptureAsync(CancellationToken.None);

        var expected = 50d / Math.Max(1, Environment.ProcessorCount);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var sample = Assert.IsType<ProcessResourceSample>(result.Value);
        Assert.Equal(expected, Assert.Single(sample.Processes).CpuPercent!.Value, 10);
        Assert.Equal(expected, sample.Statistics.ObservedCpuPercent!.Value, 10);
    }

    [Fact]
    public async Task ConsecutiveNetworkCountersProduceAggregateReceiveAndSendRates()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var processes = new SequenceProcessSnapshotSource();
        processes.Enqueue(Capture(Process(
            41,
            "asura",
            128,
            TimeSpan.FromSeconds(2),
            FirstStart)));
        processes.Enqueue(Capture(Process(
            41,
            "asura",
            128,
            TimeSpan.FromSeconds(2),
            FirstStart)));
        var network = new SequenceNetworkSnapshotSource();
        network.Enqueue(
            Interface("eth0", received: 1_000, sent: 2_000),
            Interface("wlan0", received: 500, sent: 700));
        network.Enqueue(
            Interface("eth0", received: 3_000, sent: 3_000),
            Interface("wlan0", received: 1_500, sent: 2_700),
            Interface("new0", received: 9_000, sent: 9_000));
        var sampler = new ProcessResourceSampler(processes, clock, network);

        var first = await sampler.CaptureAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        var second = await sampler.CaptureAsync(CancellationToken.None);

        Assert.Null(first.Value!.Statistics.NetworkReceivedBytesPerSecond);
        Assert.Null(first.Value.Statistics.NetworkSentBytesPerSecond);
        Assert.Equal(1_500, second.Value!.Statistics.NetworkReceivedBytesPerSecond);
        Assert.Equal(1_500, second.Value.Statistics.NetworkSentBytesPerSecond);
    }

    [Fact]
    public async Task NetworkCaptureFailureDoesNotHideProcessStatistics()
    {
        var processes = new SequenceProcessSnapshotSource();
        processes.Enqueue(Capture(Process(
            41,
            "asura",
            128,
            TimeSpan.FromSeconds(2),
            FirstStart)));
        var network = new SequenceNetworkSnapshotSource();
        network.EnqueueFailure(new IOException("network counters unavailable"));
        var sampler = new ProcessResourceSampler(
            processes,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch),
            network);

        var result = await sampler.CaptureAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(128, result.Value!.Statistics.ObservedWorkingSetBytes);
        Assert.Null(result.Value.Statistics.NetworkReceivedBytesPerSecond);
        Assert.Null(result.Value.Statistics.NetworkSentBytesPerSecond);
    }

    [Fact]
    public async Task CounterResetOnlySuppressesTheAffectedNetworkDirection()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var processes = new SequenceProcessSnapshotSource();
        processes.Enqueue(Capture(Process(
            41,
            "asura",
            128,
            TimeSpan.FromSeconds(2),
            FirstStart)));
        processes.Enqueue(Capture(Process(
            41,
            "asura",
            128,
            TimeSpan.FromSeconds(2),
            FirstStart)));
        var network = new SequenceNetworkSnapshotSource();
        network.Enqueue(Interface("eth0", received: 5_000, sent: 2_000));
        network.Enqueue(Interface("eth0", received: 1_000, sent: 3_000));
        var sampler = new ProcessResourceSampler(processes, clock, network);

        _ = await sampler.CaptureAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await sampler.CaptureAsync(CancellationToken.None);

        Assert.Null(result.Value!.Statistics.NetworkReceivedBytesPerSecond);
        Assert.Equal(1_000, result.Value.Statistics.NetworkSentBytesPerSecond);
    }

    [Fact]
    public async Task ReusedProcessIdDoesNotInheritPriorCpuTime()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(
            Process(41, "old", 128, TimeSpan.FromSeconds(2), FirstStart)));
        source.Enqueue(Capture(
            Process(
                41,
                "new",
                256,
                TimeSpan.FromSeconds(200),
                FirstStart.AddMinutes(1))));
        var sampler = new ProcessResourceSampler(source, clock);

        _ = await sampler.CaptureAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await sampler.CaptureAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(Assert.Single(result.Value!.Processes).CpuPercent);
        Assert.Null(result.Value.Statistics.ObservedCpuPercent);
    }

    [Fact]
    public async Task UnknownStartTimeCannotCreateOrPreserveACpuIdentity()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(
            Process(41, "known", 128, TimeSpan.FromSeconds(2), FirstStart)));
        source.Enqueue(Capture(
            Process(41, "unknown", 128, TimeSpan.FromSeconds(100), null)));
        source.Enqueue(Capture(
            Process(41, "known-again", 128, TimeSpan.FromSeconds(101), FirstStart)));
        var sampler = new ProcessResourceSampler(source, clock);

        _ = await sampler.CaptureAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        var unknown = await sampler.CaptureAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        var knownAgain = await sampler.CaptureAsync(CancellationToken.None);

        Assert.Null(Assert.Single(unknown.Value!.Processes).CpuPercent);
        Assert.Null(Assert.Single(knownAgain.Value!.Processes).CpuPercent);
        Assert.Null(knownAgain.Value.Statistics.ObservedCpuPercent);
    }

    [Fact]
    public async Task CaptureFailureDoesNotExposeNativeExceptionText()
    {
        const string secret = "secret --password hunter2";
        var source = new SequenceProcessSnapshotSource();
        source.EnqueueFailure(new InvalidOperationException(secret));
        var sampler = new ProcessResourceSampler(
            source,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        var result = await sampler.CaptureAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(MonitorPanelErrorCode.CaptureFailed, result.Error!.Code);
        Assert.Equal("monitor_capture_failed", result.Error.StableCode);
        Assert.True(result.Error.Retryable);
        Assert.DoesNotContain(secret, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCancelledCaptureReturnsAStableCancelledFailure()
    {
        var source = new SequenceProcessSnapshotSource();
        var sampler = new ProcessResourceSampler(
            source,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await sampler.CaptureAsync(cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(MonitorPanelErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal("monitor_cancelled", result.Error.StableCode);
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task OverlappingConsumersShareOneProcessCapture()
    {
        var captureStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new ManualResetEventSlim();
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(cancellationToken =>
        {
            captureStarted.TrySetResult();
            releaseCapture.Wait(cancellationToken);
            return Capture(
                Process(41, "shared", 128, TimeSpan.FromSeconds(2), FirstStart));
        });
        var sampler = new ProcessResourceSampler(
            source,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        var statistics = Task.Run(async () => await sampler.CaptureAsync(
            ProcessResourceConsumer.Statistics,
            CancellationToken.None));
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var processes = Task.Run(async () => await sampler.CaptureAsync(
            ProcessResourceConsumer.ProcessMonitor,
            CancellationToken.None));
        releaseCapture.Set();
        var results = await Task.WhenAll(statistics, processes);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
        Assert.Same(results[0], results[1]);
        Assert.Equal(1, source.CaptureCount);
    }

    [Fact]
    public async Task UnexpectedProjectionFailureRemainsATypedCaptureFailure()
    {
        var clock = new ThrowingTimestampTimeProvider();
        var source = new SequenceProcessSnapshotSource();
        source.Enqueue(Capture(
            Process(41, "process", 128, TimeSpan.FromSeconds(2), FirstStart)));
        var sampler = new ProcessResourceSampler(source, clock);

        var result = await sampler.CaptureAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(MonitorPanelErrorCode.CaptureFailed, result.Error!.Code);
    }

    private static RawProcessCapture Capture(params RawProcessObservation[] processes) =>
        new(
            TimeSpan.FromHours(3),
            processes.Length,
            Array.AsReadOnly(processes),
            false);

    private static RawProcessObservation Process(
        int processId,
        string name,
        long? workingSetBytes,
        TimeSpan? processorTime,
        DateTimeOffset? startedAtUtc,
        bool isAsura = false) =>
        new(
            processId,
            name,
            workingSetBytes,
            processorTime,
            startedAtUtc,
            isAsura);

    private static RawNetworkObservation Interface(
        string interfaceId,
        long received,
        long sent) =>
        new(interfaceId, received, sent);

    private sealed class ThrowingTimestampTimeProvider : TimeProvider
    {
        public override long GetTimestamp() =>
            throw new ArgumentOutOfRangeException("timestamp");
    }
}
