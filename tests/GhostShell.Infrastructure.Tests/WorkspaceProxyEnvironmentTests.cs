using System.Diagnostics;
using GhostShell.Application;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceProxyEnvironmentTests
{
    [Fact]
    public async Task Child_environment_overrides_inherited_bypass_and_uses_the_http_broker_for_remote_dns()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var projected = WorkspaceProxyEnvironment.Create(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Uri("socks5://127.0.0.1:45123"),
            new WorkspaceNetworkProxyCredentials("workspace", "test-password"),
            new Uri("http://127.0.0.1:45123"));
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        start.Environment["NO_PROXY"] = "*";
        start.Environment["no_proxy"] = "*";
        foreach (var pair in projected)
        {
            start.Environment[pair.Key] = pair.Value;
        }
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("test -z \"$NO_PROXY$no_proxy\" && test \"$HTTP_PROXY\" = \"$HTTPS_PROXY\" && test \"$HTTP_PROXY\" = \"$ALL_PROXY\" && printf ok");
        using var child = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = await child.StandardOutput.ReadToEndAsync(timeout.Token);
        await child.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, child.ExitCode);
        Assert.Equal("ok", output);
        Assert.Equal("http", new Uri(projected["HTTPS_PROXY"]).Scheme);
    }

    [Fact]
    public void A_socks_only_endpoint_is_not_falsely_advertised_as_http()
    {
        var environment = WorkspaceProxyEnvironment.Create(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Uri("socks5://127.0.0.1:45123"), null);
        Assert.Equal("socks5", new Uri(environment["HTTP_PROXY"]).Scheme);
    }
}
