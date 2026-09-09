namespace Asura.Monitoring.Tests;

public sealed class LocalPosixCommandTransportTests
{
    [Fact]
    public async Task ExecuteCapturesOutputWithoutACommandShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var transport = new LocalPosixCommandTransport();

        var result = await transport.ExecuteAsync(
            new PosixCommand(
                "/usr/bin/printf",
                ["%s", "monitor-output"],
                TimeSpan.FromSeconds(5),
                maximumOutputCharacters: 1_024),
            CancellationToken.None);

        Assert.Equal(PosixCommandOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("monitor-output", result.StandardOutput);
    }

    [Fact]
    public async Task AlreadyCancelledExecutionDoesNotStartAProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new LocalPosixCommandTransport();

        var result = await transport.ExecuteAsync(
            new PosixCommand(
                "executable-that-must-not-start",
                [],
                TimeSpan.FromSeconds(5),
                maximumOutputCharacters: 1_024),
            cancellation.Token);

        Assert.Equal(PosixCommandOutcome.Cancelled, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Empty(result.StandardOutput);
    }
}
