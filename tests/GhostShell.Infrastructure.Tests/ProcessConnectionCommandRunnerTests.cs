namespace GhostShell.Infrastructure.Tests;

public sealed class ProcessConnectionCommandRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_timeout_reap_the_owned_process_before_returning(bool timeout)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Directory.CreateTempSubdirectory("ghostshell-process-reap-");
        try
        {
            var pidPath = Path.Combine(root.FullName, "pid");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var runner = new ProcessConnectionCommandRunner();
            // A fixed test command writes only the owned child's PID. No input is
            // interpreted as shell syntax; the path is a positional argument.
            var run = runner.RunAsync(new ConnectionProbeCommand("/bin/sh",
                ["-c", "echo $$ > \"$1\"; exec /bin/sleep 30", "reap-test", pidPath],
                timeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(20)), cancellation.Token).AsTask();
            while (!File.Exists(pidPath))
            {
                await Task.Delay(10, cancellation.Token);
            }
            var pid = int.Parse(await File.ReadAllTextAsync(pidPath, cancellation.Token), System.Globalization.CultureInfo.InvariantCulture);
            if (!timeout)
            {
                await cancellation.CancelAsync();
            }
            var result = await run;
            Assert.Equal(timeout ? ConnectionProbeOutcome.TimedOut : ConnectionProbeOutcome.Cancelled, result.Outcome);
            var status = await runner.RunAsync(new ConnectionProbeCommand("/bin/ps",
                ["-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", "pid="], TimeSpan.FromSeconds(2)), CancellationToken.None);
            Assert.Equal(1, status.ExitCode);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
