using System.Diagnostics;
using System.Globalization;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceGatewayProcessRunnerTests
{
    [Fact]
    public async Task Exited_parent_with_inherited_output_does_not_block_disposal()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("ghostshell-orphan-test-").FullName;
        var pidPath = Path.Combine(directory, "pid");
        Process? child = null;
        Task? disposal = null;
        try
        {
            var started = await new WorkspaceGatewayProcessRunner().StartAsync(
                new WorkspaceGatewayProcessRequest(
                    "/bin/sh",
                    ["-c", "sleep 30 & printf '%s' \"$!\" > \"$1\"; printf 'READY v1\\n'; sleep 0.2; exit 1", "orphan-test", pidPath],
                    ReadOnlyMemory<byte>.Empty),
                TimeSpan.FromSeconds(5), CancellationToken.None);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidPath), CultureInfo.InvariantCulture));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!started.Process.HasExited)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }

            disposal = started.Process.DisposeAsync().AsTask();
            await disposal.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(child.HasExited);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                }

                child.Dispose();
            }

            if (disposal is not null)
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_stdin_write_respects_cancellation_and_deadline_and_stops_child(bool cancelCaller)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var child = new ChildCanary();
        using var cancellation = new CancellationTokenSource();
        var command = new WorkspaceGatewayProcessRunner().RunAsync(
            child.Request("exec sleep 60"),
            cancelCaller ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2),
            cancellation.Token).AsTask();
        await child.ObserveStartedAsync();
        if (cancelCaller)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => command.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("did not finish in time", exception.Message, StringComparison.Ordinal);
        }

        await child.AssertExitedAsync();
    }

    [Fact]
    public async Task Child_output_is_drained_while_stdin_is_being_written()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var child = new ChildCanary();
        var command = new WorkspaceGatewayProcessRunner().RunAsync(
            child.Request("head -c 131072 /dev/zero; head -c 131072 /dev/zero >&2; cat >/dev/null"),
            TimeSpan.FromSeconds(5), CancellationToken.None).AsTask();
        await child.ObserveStartedAsync();
        Assert.Equal(0, (await command.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
        await child.AssertExitedAsync();
    }

    [Fact]
    public async Task Failed_stdin_write_stops_child()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var child = new ChildCanary();
        var command = new WorkspaceGatewayProcessRunner().RunAsync(
            child.Request("exec 0<&-; exec sleep 60"), TimeSpan.FromSeconds(30), CancellationToken.None).AsTask();
        await child.ObserveStartedAsync();
        await Assert.ThrowsAsync<IOException>(() => command.WaitAsync(TimeSpan.FromSeconds(5)));
        await child.AssertExitedAsync();
    }

    private sealed class ChildCanary : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("ghostshell-runner-test-").FullName;
        private Process? _process;

        public WorkspaceGatewayProcessRequest Request(string script) => new(
            "/bin/sh",
            ["-c", "printf '%s' \"$$\" > \"$1\"; " + script, "ghostshell-runner-test", Path.Combine(_directory, "pid")],
            new byte[8 * 1024 * 1024]);

        public async Task ObserveStartedAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var path = Path.Combine(_directory, "pid");
            while (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }

            var pid = int.Parse(await File.ReadAllTextAsync(path, deadline.Token), CultureInfo.InvariantCulture);
            try
            {
                _process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                // A broken stdin pipe can be handled before the canary observes the process.
            }
        }

        public async Task AssertExitedAsync()
        {
            if (_process is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(deadline.Token);
                Assert.True(_process.HasExited);
            }
        }

        public void Dispose()
        {
            if (_process is not null)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                _process.Dispose();
            }

            Directory.Delete(_directory, recursive: true);
        }
    }
}
