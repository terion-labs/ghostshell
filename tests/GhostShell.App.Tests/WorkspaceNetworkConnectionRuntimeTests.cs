using GhostShell.App;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class WorkspaceNetworkConnectionRuntimeTests
{
    [Fact]
    public async Task Host_launch_receives_standard_proxy_environment_variables()
    {
        var state = new WorkspaceNetworkEgressState();
        var proxy = WorkspaceNetworkEgress.ViaProxy(
            new Uri("socks5://127.0.0.1:45123"));
        state.Apply(proxy);
        var runtime = new WorkspaceNetworkConnectionRuntime(
            new StubRuntime(),
            state,
            injectProxyEnvironment: true);

        var result = await runtime.PlanOpenAsync(
            Profile(),
            progress: null,
            CancellationToken.None);

        var plan = Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Success>(result).Value;
        Assert.Equal(proxy.ProxyEndpoint?.AbsoluteUri, plan.Launch.Environment["ALL_PROXY"]);
        Assert.Equal(proxy.ProxyEndpoint?.AbsoluteUri, plan.Launch.Environment["HTTPS_PROXY"]);
        Assert.Equal(proxy.ProxyEndpoint?.AbsoluteUri, plan.Launch.Environment["HTTP_PROXY"]);
        Assert.Equal("kept", plan.Launch.Environment["EXISTING"]);
        Assert.Equal(string.Empty, plan.Launch.Environment["NO_PROXY"]);
        Assert.Equal(string.Empty, plan.Launch.Environment["no_proxy"]);
        Assert.Contains("--ghostshell-workspace-ssh", plan.Launch.Environment["GIT_SSH_COMMAND"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_switch_rejects_new_launches()
    {
        var state = new WorkspaceNetworkEgressState();
        state.Apply(WorkspaceNetworkEgress.Blocked);
        var runtime = new WorkspaceNetworkConnectionRuntime(
            new StubRuntime(),
            state,
            injectProxyEnvironment: true);

        var result = await runtime.PlanOpenAsync(
            Profile(),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Failure>(result);
        Assert.Equal("workspace_network_kill_switch_blocked", failure.Error.StableCode);
    }

    [Fact]
    public async Task Host_broker_http_endpoint_is_projected_without_changing_the_ssh_socks_route()
    {
        var state = new WorkspaceNetworkEgressState();
        state.SetLocalProxyEndpoint(
            new Uri("socks5://127.0.0.1:45124"),
            new WorkspaceNetworkProxyCredentials("workspace", "secret"),
            new Uri("http://127.0.0.1:45124"));
        var runtime = new WorkspaceNetworkConnectionRuntime(new StubRuntime(), state, injectProxyEnvironment: true);
        var result = await runtime.PlanOpenAsync(SshProfile(), progress: null, CancellationToken.None);
        var launch = Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Success>(result).Value.Launch;
        Assert.Equal("http://workspace:secret@127.0.0.1:45124/", launch.Environment["HTTPS_PROXY"]);
        Assert.Contains("--ghostshell-workspace-socks-connect 45124", launch.Arguments[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_ssh_launch_uses_the_workspace_socks_helper()
    {
        var state = new WorkspaceNetworkEgressState();
        state.SetLocalProxyEndpoint(
            new Uri("socks5://127.0.0.1:45124"),
            new WorkspaceNetworkProxyCredentials("workspace", "secret"));
        var runtime = new WorkspaceNetworkConnectionRuntime(
            new StubRuntime(),
            state,
            injectProxyEnvironment: true);

        var result = await runtime.PlanOpenAsync(
            SshProfile(),
            progress: null,
            CancellationToken.None);

        var plan = Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Success>(result).Value;
        Assert.Equal("-o", plan.Launch.Arguments[0]);
        Assert.Contains(
            "--ghostshell-workspace-socks-connect 45124",
            plan.Launch.Arguments[1],
            StringComparison.Ordinal);
        Assert.Equal(
            "socks5://workspace:secret@127.0.0.1:45124/",
            plan.Launch.Environment["ALL_PROXY"]);
    }

    [Fact]
    public async Task Isolated_launch_does_not_receive_a_host_loopback_proxy()
    {
        var state = new WorkspaceNetworkEgressState();
        state.Apply(WorkspaceNetworkEgress.ViaProxy(
            new Uri("socks5://127.0.0.1:45123")));
        var runtime = new WorkspaceNetworkConnectionRuntime(
            new StubRuntime(),
            state,
            injectProxyEnvironment: false);

        var result = await runtime.PlanOpenAsync(
            Profile(),
            progress: null,
            CancellationToken.None);

        var plan = Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Success>(result).Value;
        Assert.False(plan.Launch.Environment.ContainsKey("ALL_PROXY"));
    }

    [Fact]
    public async Task Direct_connection_test_uses_the_host_runtime()
    {
        var inner = new StubRuntime();
        var runtime = new WorkspaceNetworkConnectionRuntime(
            inner,
            new WorkspaceNetworkEgressState(),
            injectProxyEnvironment: true);

        var result = await runtime.TestAsync(
            Profile(),
            progress: null,
            CancellationToken.None);

        Assert.IsType<ConnectionRuntimeResult<ConnectionTestReport>.Success>(result);
        Assert.Equal(1, inner.TestCount);
    }

    [Fact]
    public async Task Kill_switch_blocks_connection_test_without_using_the_host_runtime()
    {
        var state = new WorkspaceNetworkEgressState();
        state.Apply(WorkspaceNetworkEgress.Blocked);
        var inner = new StubRuntime();
        var runtime = new WorkspaceNetworkConnectionRuntime(
            inner,
            state,
            injectProxyEnvironment: true);

        var result = await runtime.TestAsync(
            Profile(),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<ConnectionRuntimeResult<ConnectionTestReport>.Failure>(result);
        Assert.Equal("workspace_network_kill_switch_blocked", failure.Error.StableCode);
        Assert.Equal(0, inner.TestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_route_rejects_unrouted_connection_test(bool attached)
    {
        var state = new WorkspaceNetworkEgressState();
        state.Apply(attached
            ? WorkspaceNetworkEgress.Attached
            : WorkspaceNetworkEgress.ViaProxy(new Uri("socks5://127.0.0.1:45123")));
        var inner = new StubRuntime();
        var runtime = new WorkspaceNetworkConnectionRuntime(
            inner,
            state,
            injectProxyEnvironment: true);

        var result = await runtime.TestAsync(
            Profile(),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<ConnectionRuntimeResult<ConnectionTestReport>.Failure>(result);
        Assert.Equal("workspace_network_route_test_unavailable", failure.Error.StableCode);
        Assert.Equal(0, inner.TestCount);
    }

    private static ConnectionProfile Profile() => new(
        new ConnectionId("network-runtime-test"),
        ConnectionProfile.CurrentSchemaVersion,
        "Local",
        new ConnectionEndpoint.Local(),
        new ConnectionAuthentication.None(),
        new ConnectionStartup(),
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);

    private static ConnectionProfile SshProfile() => new(
        new ConnectionId("network-runtime-ssh-test"),
        ConnectionProfile.CurrentSchemaVersion,
        "SSH",
        new ConnectionEndpoint.Ssh("server.example.test", 22, "tester"),
        new ConnectionAuthentication.SshAgent(),
        new ConnectionStartup(),
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.AcceptNew);

    private sealed class StubRuntime : IConnectionRuntime
    {
        public int TestCount { get; private set; }

        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    profile.Id,
                    profile.ConnectionKind,
                    new TerminalLaunchRequest(
                        workingDirectory: null,
                        executable: "/bin/sh",
                        environment: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["EXISTING"] = "kept",
                            ["NO_PROXY"] = "example.test",
                            ["no_proxy"] = "localhost",
                        }),
                    ConnectionAuthenticationMode.None,
                    profile.HostKeyPolicy,
                    ConnectionReconnectMode.NotApplicable)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            TestCount++;
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Succeed(
                new ConnectionTestReport(
                    profile.Id,
                    profile.ConnectionKind,
                    ConnectionTestVerification.RuntimeAvailable,
                    endpointReached: false)));
        }
    }
}
