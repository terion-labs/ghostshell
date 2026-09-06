using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceIsolationSelectedRouteTests
{
    [Fact]
    public async Task BrowserProxyPlansItsRelayThroughTheSelectedConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = SshConnection();
        var runtime = new RecordingCommandRuntime();
        await using var proxy = new WorkspaceIsolationSocksProxy(runtime, connection);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort, timeout.Token);
        var stream = client.GetStream();

        await AuthenticateAsync(stream, proxy.LocalProxyCredentials, timeout.Token);
        var host = Encoding.ASCII.GetBytes("example.com");
        byte[] request = [5, 1, 0, 3, checked((byte)host.Length), .. host, 0, 80];
        await stream.WriteAsync(request, timeout.Token);

        Assert.Equal(connection.Id, await runtime.PlannedConnection.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task Proxy_does_not_send_a_failure_reply_after_connect_succeeded()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var closeUpstream = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamSession = ServeThenResetSocksConnectionAsync(
            upstream,
            closeUpstream.Task,
            timeout.Token);
        await using var proxy = new WorkspaceIsolationSocksProxy(
            new RecordingCommandRuntime(),
            SshConnection());
        proxy.Apply(WorkspaceNetworkEgress.ViaProxy(
            new Uri($"socks5://127.0.0.1:{upstreamPort}")));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort, timeout.Token);
        var stream = client.GetStream();

        await AuthenticateAsync(stream, proxy.LocalProxyCredentials, timeout.Token);
        var host = Encoding.ASCII.GetBytes("example.com");
        byte[] request = [5, 1, 0, 3, checked((byte)host.Length), .. host, 1, 187];
        await stream.WriteAsync(request, timeout.Token);
        var success = new byte[10];
        await stream.ReadExactlyAsync(success, timeout.Token);
        Assert.Equal((byte)0, success[1]);

        closeUpstream.TrySetResult();
        await upstreamSession;
        client.Client.Shutdown(SocketShutdown.Send);
        var extraReply = new byte[10];
        var extraLength = await stream.ReadAsync(extraReply, timeout.Token);

        Assert.Equal(0, extraLength);
    }

    [Fact]
    public async Task DatabaseTunnelPlansItsRelayThroughTheSelectedConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = SshConnection();
        var runtime = new RecordingCommandRuntime();
        var factory = new WorkspaceIsolationTcpTunnelFactory(runtime);
        await using var tunnel = await factory.OpenAsync(
            connection,
            "database.internal",
            5432,
            timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, timeout.Token);

        Assert.Equal(connection.Id, await runtime.PlannedConnection.WaitAsync(timeout.Token));
    }

    private static ConnectionProfile SshConnection() => new(
        new ConnectionId("isolated-route-test"),
        ConnectionProfile.CurrentSchemaVersion,
        "External server",
        new ConnectionEndpoint.Ssh("example.com", username: "tester"),
        new ConnectionAuthentication.None(),
        new ConnectionStartup(),
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.InsecureIgnore);

    private static async Task AuthenticateAsync(
        Stream stream,
        WorkspaceNetworkProxyCredentials credentials,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 2 }, cancellationToken);
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, cancellationToken);
        Assert.Equal(new byte[] { 5, 2 }, greeting);
        var username = Encoding.UTF8.GetBytes(credentials.Username);
        var password = Encoding.UTF8.GetBytes(credentials.Password);
        byte[] authentication =
        [
            1,
            checked((byte)username.Length),
            .. username,
            checked((byte)password.Length),
            .. password,
        ];
        await stream.WriteAsync(authentication, cancellationToken);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, cancellationToken);
        Assert.Equal(new byte[] { 1, 0 }, reply);
    }

    private static async Task ServeThenResetSocksConnectionAsync(
        TcpListener listener,
        Task closeConnection,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken);
        var requestHeader = new byte[5];
        await stream.ReadExactlyAsync(requestHeader, cancellationToken);
        var addressAndPort = new byte[requestHeader[4] + 2];
        await stream.ReadExactlyAsync(addressAndPort, cancellationToken);
        await stream.WriteAsync(
            new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 },
            cancellationToken);
        await closeConnection.WaitAsync(cancellationToken);
        client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
    }

    private sealed class RecordingCommandRuntime : IConnectionCommandRuntime
    {
        private readonly TaskCompletionSource<ConnectionId> _plannedConnection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ConnectionId> PlannedConnection => _plannedConnection.Task;

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
            ConnectionProfile connection,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            _ = executable;
            _ = arguments;
            cancellationToken.ThrowIfCancellationRequested();
            _plannedConnection.TrySetResult(connection.Id);
            return ValueTask.FromResult(ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(
                new ConnectionRuntimeError(
                    ConnectionRuntimeErrorCode.ProcessFailed,
                    "test.relay-stopped",
                    "The test stopped before launching a relay.",
                    Retryable: false,
                    ConnectionRecoveryAction.None)));
        }

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(
            ConnectionProfile connection,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            PlanCommandAsync(connection, executable, arguments, cancellationToken);
    }
}
