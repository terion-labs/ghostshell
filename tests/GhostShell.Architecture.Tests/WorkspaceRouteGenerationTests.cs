using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.App;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceRouteGenerationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_before_accept_rejects_old_generation_credentials_without_new_route_dial(bool isolated)
    {
        await using var owner = CreateBroker(isolated);
        var connector = (IWorkspaceNetworkConnector)owner;
        var snapshot = connector.CaptureRoute();
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        ((IWorkspaceNetworkEgressSink)owner).Apply(WorkspaceNetworkEgress.ViaProxy(
            new Uri($"socks5://127.0.0.1:{((IPEndPoint)upstream.LocalEndpoint).Port}")));
        Assert.True(snapshot.RouteLifetime.IsCancellationRequested);

        // Bypass the snapshot's early cancellation check to exercise listener
        // admission itself, as SSH.NET owns its socket and SOCKS handshake.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<IOException>(async () =>
            await WorkspaceSocksClient.ConnectAsync(connector.LocalProxyEndpoint.Port,
                snapshot.LocalProxyCredentials!, "synthetic.invalid", 1433, timeout.Token));
        Assert.False(upstream.Pending());
        Assert.False(snapshot is IWorkspaceNetworkEgressSink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_after_server_capture_cancels_old_handshake_without_new_route_dial(bool isolated)
    {
        await using var owner = CreateBroker(isolated);
        var connector = (IWorkspaceNetworkConnector)owner;
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(IPAddress.Loopback, connector.LocalProxyEndpoint.Port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 }, timeout.Token);
        var response = new byte[2];
        await stream.ReadExactlyAsync(response, timeout.Token);
        Assert.Equal(new byte[] { 5, 2 }, response); // Serve has captured its route.

        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        ((IWorkspaceNetworkEgressSink)owner).Apply(WorkspaceNetworkEgress.ViaProxy(
            new Uri($"socks5://127.0.0.1:{((IPEndPoint)upstream.LocalEndpoint).Port}")));
        Assert.Equal(0, await stream.ReadAsync(response, timeout.Token));
        Assert.False(upstream.Pending());
    }

    [Fact]
    public async Task Captured_database_factory_passes_the_same_frozen_route_to_ssh()
    {
        await using var proxy = new HostWorkspaceSocksProxy();
        IWorkspaceNetworkConnector? sshRoute = null;
        var factory = new WorkspaceNetworkDatabaseTunnelFactory(proxy, route =>
        {
            sshRoute = route;
            return new RejectingSshFactory();
        });
        var captured = factory.CaptureRoute();
        var connection = new ConnectionProfile(
            new ConnectionId("synthetic-ssh"), 1, "Synthetic SSH",
            new ConnectionEndpoint.Ssh("synthetic.invalid", 22, "user"),
            new ConnectionAuthentication.None(), BuiltInConnections.Local.Startup,
            BuiltInConnections.Local.KeepAlive, SshHostKeyPolicy.Strict);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await captured.OpenAsync(connection, "database.invalid", 1433, CancellationToken.None));
        Assert.NotNull(sshRoute);
        Assert.Equal(captured.RouteLifetime, sshRoute.RouteLifetime);
        Assert.NotEqual(proxy.LocalProxyCredentials.Password, sshRoute.LocalProxyCredentials!.Password, StringComparer.Ordinal);
        proxy.Apply(WorkspaceNetworkEgress.Blocked);
        Assert.True(captured.RouteLifetime.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await captured.OpenAsync(connection, "redirect.invalid", 15433, CancellationToken.None));
    }

    [Fact]
    public async Task Current_snapshot_and_stable_browser_credentials_work_after_apply()
    {
        await using var proxy = new HostWorkspaceSocksProxy();
        var stableCredentials = proxy.LocalProxyCredentials;
        var oldSnapshot = proxy.CaptureRoute();
        proxy.Apply(WorkspaceNetworkEgress.Blocked);
        proxy.Apply(WorkspaceNetworkEgress.Direct);
        var current = proxy.CaptureRoute();
        Assert.Same(stableCredentials, proxy.LocalProxyCredentials);
        Assert.NotEqual(oldSnapshot.LocalProxyCredentials!.Password, current.LocalProxyCredentials!.Password, StringComparer.Ordinal);
        Assert.NotEqual(stableCredentials.Password, current.LocalProxyCredentials.Password, StringComparer.Ordinal);
        foreach (var credentials in new[] { current.LocalProxyCredentials, stableCredentials })
        {
            using var destination = new TcpListener(IPAddress.Loopback, 0);
            destination.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var accepted = destination.AcceptTcpClientAsync(timeout.Token);
            await using var stream = await WorkspaceSocksClient.ConnectAsync(proxy.LocalPort,
                credentials, "127.0.0.1", ((IPEndPoint)destination.LocalEndpoint).Port, timeout.Token);
            using var server = await accepted;
            await server.GetStream().WriteAsync("proof"u8.ToArray(), timeout.Token);
            var response = new byte[5];
            await stream.ReadExactlyAsync(response, timeout.Token);
            Assert.Equal("proof", Encoding.ASCII.GetString(response));
        }
    }

    private static IAsyncDisposable CreateBroker(bool isolated) => isolated
        ? new WorkspaceIsolationSocksProxy(new RejectingCommandRuntime(), BuiltInConnections.Local)
        : new HostWorkspaceSocksProxy();

    private sealed class RejectingCommandRuntime : IConnectionCommandRuntime
    {
        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
            ConnectionProfile connection, string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No isolate command may run in this test.");

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(
            ConnectionProfile connection, string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No isolate command may run in this test.");
    }

    private sealed class RejectingSshFactory : IDatabaseTunnelFactory
    {
        public ValueTask<IDatabaseTunnelLease> OpenAsync(ConnectionProfile connection,
            string targetHost, int targetPort, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
