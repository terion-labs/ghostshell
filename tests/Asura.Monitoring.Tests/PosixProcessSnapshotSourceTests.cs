namespace Asura.Monitoring.Tests;

public sealed class PosixProcessSnapshotSourceTests
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 7, 28, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CaptureUsesStructuredPsAndParsesPortableProcessColumns()
    {
        var transport = new RecordingPosixCommandTransport();
        transport.Results.Enqueue(Success(
            """
                1  28064  40:51.61 03-09:50:00 /sbin/launchd
              231 182816   0:07.56 02-08:21:50 /Applications/Brave Browser Helper (Renderer)
              902      -      nope       01:30 kernel worker
            """));
        var source = new PosixProcessSnapshotSource(
            transport,
            new ManualTimeProvider(CapturedAt),
            localProcessId: 231);

        var capture = await source.CaptureAsync(CancellationToken.None);

        var command = Assert.Single(transport.Commands);
        Assert.Equal("ps", command.Executable);
        Assert.Equal(
            ["-A", "-o", "pid=", "-o", "rss=", "-o", "time=", "-o", "etime=", "-o", "comm="],
            command.Arguments);
        Assert.Equal(3, capture.EnumeratedProcessCount);
        Assert.False(capture.IsTruncated);
        Assert.Equal(TimeSpan.FromDays(3) + TimeSpan.FromHours(9) + TimeSpan.FromMinutes(50), capture.HostUptime);

        var browser = capture.Processes[1];
        Assert.Equal(231, browser.ProcessId);
        Assert.Equal("Brave Browser Helper (Renderer)", browser.Name);
        Assert.Equal(182816L * 1024, browser.WorkingSetBytes);
        Assert.Equal(TimeSpan.FromSeconds(7.56), browser.TotalProcessorTime);
        Assert.Equal(
            CapturedAt - TimeSpan.FromDays(2) - TimeSpan.FromHours(8)
                - TimeSpan.FromMinutes(21) - TimeSpan.FromSeconds(50),
            browser.StartedAtUtc);
        Assert.True(browser.IsAsura);

        var partial = capture.Processes[2];
        Assert.Null(partial.WorkingSetBytes);
        Assert.Null(partial.TotalProcessorTime);
        Assert.Equal("kernel worker", partial.Name);
    }

    [Theory]
    [InlineData("07:05", 425)]
    [InlineData("02:07:05", 7625)]
    [InlineData("3-02:07:05", 266825)]
    [InlineData("40:51.61", 2451.61)]
    public void DurationParserAcceptsPsTimeAndElapsedFormats(
        string value,
        double expectedSeconds)
    {
        Assert.True(PosixProcessSnapshotSource.TryParseDuration(value, out var duration));
        Assert.Equal(expectedSeconds, duration.TotalSeconds, precision: 6);
    }

    [Theory]
    [InlineData("2147483647-00:00:00")]
    [InlineData("10675200-00:00:00")]
    public void DurationParserRejectsValuesBeyondTimeSpanRange(string value)
    {
        Assert.False(PosixProcessSnapshotSource.TryParseDuration(value, out _));
    }

    [Fact]
    public async Task ElapsedValueBeforeDateTimeMinimumDoesNotRejectTheSnapshot()
    {
        var transport = new RecordingPosixCommandTransport();
        transport.Results.Enqueue(Success(
            "1 1024 00:01 1000000-00:00:00 /sbin/init"));
        var source = new PosixProcessSnapshotSource(
            transport,
            new ManualTimeProvider(CapturedAt),
            localProcessId: null);

        var capture = await source.CaptureAsync(CancellationToken.None);

        var process = Assert.Single(capture.Processes);
        Assert.Null(process.StartedAtUtc);
        Assert.Equal(TimeSpan.Zero, capture.HostUptime);
    }

    [Fact]
    public async Task MissingPsProducesAnUnsupportedMonitorSource()
    {
        var transport = new RecordingPosixCommandTransport();
        transport.Results.Enqueue(new PosixCommandResult(
            PosixCommandOutcome.StartFailed,
            null,
            string.Empty));
        var source = new PosixProcessSnapshotSource(
            transport,
            new ManualTimeProvider(CapturedAt),
            localProcessId: null);

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            async () => await source.CaptureAsync(CancellationToken.None));
    }

    private static PosixCommandResult Success(string output) =>
        new(PosixCommandOutcome.Exited, 0, output);
}
