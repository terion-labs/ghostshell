using System.Text.Json;

namespace GhostShell.Infrastructure.Tests;

public sealed class HostVpnProcessReadinessTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(65536)]
    public async Task Structured_stdout_is_not_corrupted_or_displaced_by_stderr(int warningLength)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runner = new HostUserspaceVpnProcessRunner();
        var result = await runner.RunAsync(new HostVpnProcessRequest(
            "/bin/sh",
            ["-c", $"printf 'Warning: ' >&2; printf '%{warningLength}d' 0 >&2; printf '{{\"BackendState\":\"Running\"}}'"],
            ReadOnlyMemory<byte>.Empty), timeout.Token);

        Assert.Equal(0, result.ExitCode);
        using var status = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("Running", status.RootElement.GetProperty("BackendState").GetString());
        Assert.Equal(Math.Min("Warning: ".Length + warningLength, 32 * 1024), result.StandardError.Length);
        // The old readiness path parsed this mixed stream and rejected a
        // healthy daemon whenever the CLI also emitted a warning.
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(result.Diagnostic));
    }

    [Theory]
    [InlineData("printf 'READY v1 '; sleep 0.01; printf 'socks5 split-routes\\n'", true)]
    [InlineData("printf 'READY v1 socks5 split-routes\\n' >&2", false)]
    [InlineData("printf 'not READY v1 socks5 split-routes\\n'", false)]
    [InlineData("printf '%0130dREADY v1 socks5 split-routes\\n' 0", false)]
    [InlineData("printf 'READY v1 socks5 split-routes'", false)]
    public async Task Readiness_requires_a_complete_bounded_stdout_protocol_line(string command, bool expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new HostUserspaceVpnProcessRunner();
        await using var process = await runner.StartAsync(
            new HostVpnProcessRequest("/bin/sh", ["-c", command], ReadOnlyMemory<byte>.Empty),
            CancellationToken.None);
        Assert.Equal(expected, await process.RouteReady.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Disposing_a_process_releases_its_pending_readiness_wait()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new HostUserspaceVpnProcessRunner();
        await using var process = await runner.StartAsync(
            new HostVpnProcessRequest("/bin/sh", ["-c", "sleep 30"], ReadOnlyMemory<byte>.Empty),
            CancellationToken.None);
        Assert.False(process.RouteReady.IsCompleted);
        await process.DisposeAsync();
        Assert.False(await process.RouteReady.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
