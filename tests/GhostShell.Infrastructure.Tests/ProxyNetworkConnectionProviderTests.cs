using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class ProxyNetworkConnectionProviderTests
{
    private static readonly NetworkConnectionId ConnectionId = new("proxy-test");

    [Fact]
    public async Task Socks_adapter_routes_connect_and_payload_through_the_upstream_proxy()
    {
        using var vault = new InMemorySecretVault();
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var observed = ServeSocksUpstreamAsync(upstream);
        var provider = CreateProvider(vault);

        await using var session = Success(await provider.ConnectAsync(
            Request(new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Socks5,
                "127.0.0.1",
                upstreamPort)),
            progress: null,
            CancellationToken.None));
        using var client = await ConnectAdapterAsync(session);
        var stream = client.GetStream();
        await NegotiateAdapterAsync(stream, "service.example", 8443);
        await stream.WriteAsync("ping"u8.ToArray());
        var reply = new byte[4];
        await ReadRequiredAsync(stream, reply);

        Assert.Equal("pong", Encoding.ASCII.GetString(reply));
        Assert.Equal(("service.example", 8443), await observed);
    }

    [Fact]
    public async Task Http_adapter_resolves_scoped_password_and_sends_basic_authentication()
    {
        using var vault = new InMemorySecretVault();
        var passwordReference = new SecretRef("proxy-password");
        var scope = new SecretScope(SecretScopeKind.NetworkConnection, ConnectionId.Value);
        using (var material = SecretMaterial.CopyFrom("secret-value"u8))
        {
            _ = Success(await vault.CreateAsync(
                new CreateSecretRequest(
                    passwordReference,
                    "Proxy password",
                    SecretKind.Password,
                    scope,
                    new SecretUsePurpose(SecretUseKind.UserManagement, ConnectionId.Value)),
                material,
                CancellationToken.None));
        }

        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var observed = ServeHttpUpstreamAsync(upstream);
        var provider = CreateProvider(vault);
        await using var session = Success(await provider.ConnectAsync(
            Request(new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Http,
                "127.0.0.1",
                upstreamPort,
                "proxy-user",
                passwordReference)),
            progress: null,
            CancellationToken.None));

        using var client = await ConnectAdapterAsync(session);
        await NegotiateAdapterAsync(client.GetStream(), "private.example", 443);
        var requestHeader = await observed;

        Assert.Contains("CONNECT private.example:443 HTTP/1.1", requestHeader, StringComparison.Ordinal);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("proxy-user:secret-value"));
        Assert.Contains($"Proxy-Authorization: Basic {encoded}", requestHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_adapter_uses_a_session_only_password_when_none_is_stored()
    {
        using var vault = new InMemorySecretVault();
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var observed = ServeHttpUpstreamAsync(upstream);
        var provider = CreateProvider(vault);
        var password = SecretMaterial.CopyFrom("session-password"u8);
        var result = await provider.ConnectAsync(
            Request(
                new NetworkConnectionConfiguration.Proxy(
                    NetworkProxyProtocol.Http,
                    "127.0.0.1",
                    upstreamPort,
                    "proxy-user"),
                password),
            progress: null,
            CancellationToken.None);
        password.Dispose();

        await using var session = Success(result);
        using var client = await ConnectAdapterAsync(session);
        await NegotiateAdapterAsync(client.GetStream(), "private.example", 443);
        var requestHeader = await observed;

        var encoded = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("proxy-user:session-password"));
        Assert.Contains(
            $"Proxy-Authorization: Basic {encoded}",
            requestHeader,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_password_is_a_typed_configuration_failure()
    {
        using var vault = new InMemorySecretVault();
        var provider = CreateProvider(vault);

        var result = await provider.ConnectAsync(
            Request(new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Http,
                "proxy.example",
                8080,
                "proxy-user",
                new SecretRef("missing-password"))),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(result);
        Assert.Equal(NetworkConnectionErrorCode.InvalidConfiguration, failure.Error.Code);
        Assert.Equal("proxy_secret_unavailable", failure.Error.StableCode);
    }

    [Fact]
    public async Task Isolated_proxy_is_rejected_before_credential_access()
    {
        using var vault = new InMemorySecretVault();
        var provider = CreateProvider(vault);
        var profile = new NetworkConnectionProfile(
            ConnectionId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Test proxy",
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Http,
                "proxy.example",
                8080,
                "proxy-user",
                new SecretRef("missing-password")));
        var request = new NetworkConnectionStartRequest(
            new WorkspaceInstanceId("workspace-test"),
            profile,
            WorkspaceNetworkPlacement.Isolated(new WorkspaceIsolationBinding(
                new WorkspaceId("workspace-definition"),
                new WorkspaceIsolationProviderId("test-isolation"),
                WorkspaceIsolationCapability.DedicatedNetworkNamespace
                | WorkspaceIsolationCapability.StructuredProcessExecution,
                "test-isolate",
                [],
                Guid.NewGuid())),
            killSwitchEnabled: false);

        var result = await provider.ConnectAsync(
            request,
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.RouteUnavailable, failure.Error.Code);
        Assert.Equal("proxy_isolated_routing_unavailable", failure.Error.StableCode);
        Assert.False(failure.Error.Retryable);
    }

    [Fact]
    public async Task Connected_result_requires_successful_end_to_end_route_probe()
    {
        using var vault = new InMemorySecretVault();
        var probe = new RecordingSocksReachabilityProbe(reachable: true);
        var progress = new RecordingProgress<NetworkConnectionProgress>();
        var provider = CreateProvider(vault, probe);

        await using var session = Success(await provider.ConnectAsync(
            Request(new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Socks5,
                "proxy.example",
                1080)),
            progress,
            CancellationToken.None));

        Assert.Equal(NetworkConnectionState.Connected, session.Snapshot.State);
        Assert.Single(probe.Ports);
        Assert.Contains(
            progress.Items,
            item => item.Status.Contains("reachability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unreachable_route_returns_actionable_failure_and_closes_adapter()
    {
        using var vault = new InMemorySecretVault();
        var probe = new RecordingSocksReachabilityProbe(reachable: false);
        var provider = CreateProvider(vault, probe);

        var result = await provider.ConnectAsync(
            Request(new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Socks5,
                "proxy.example",
                1080)),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.ConnectionFailed, failure.Error.Code);
        Assert.Equal("proxy_route_probe_failed", failure.Error.StableCode);
        Assert.True(failure.Error.Retryable);
        Assert.Contains("address", failure.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credentials", failure.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allowed destinations", failure.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("firewall", failure.Error.Message, StringComparison.OrdinalIgnoreCase);

        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(async () =>
            await client.ConnectAsync(IPAddress.Loopback, Assert.Single(probe.Ports)));
    }

    [Fact]
    public async Task Cancelled_probe_returns_typed_failure_and_closes_adapter()
    {
        using var vault = new InMemorySecretVault();
        var probe = new RecordingSocksReachabilityProbe(reachable: true)
        {
            WaitForCancellation = true,
        };
        var provider = CreateProvider(vault, probe);
        using var cancellation = new CancellationTokenSource();

        var connecting = provider.ConnectAsync(
                Request(new NetworkConnectionConfiguration.Proxy(
                    NetworkProxyProtocol.Socks5,
                    "proxy.example",
                    1080)),
                progress: null,
                cancellation.Token)
            .AsTask();
        await probe.Started.Task;
        await cancellation.CancelAsync();
        var result = await connecting;

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.Cancelled, failure.Error.Code);
        Assert.Equal("proxy_route_probe_cancelled", failure.Error.StableCode);
        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(async () =>
            await client.ConnectAsync(IPAddress.Loopback, Assert.Single(probe.Ports)));
    }

    private static ProxyNetworkConnectionProvider CreateProvider(
        ISecretVault vault,
        ISocksReachabilityProbe? reachabilityProbe = null) => new(
        vault,
        new WorkspaceTcpConnector(isolationProvider: null),
        reachabilityProbe ?? new RecordingSocksReachabilityProbe(reachable: true));

    private static NetworkConnectionStartRequest Request(
        NetworkConnectionConfiguration.Proxy configuration,
        SecretMaterial? transientPassword = null) => new(
        new WorkspaceInstanceId("workspace-test"),
        new NetworkConnectionProfile(
            ConnectionId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Test proxy",
            configuration),
        WorkspaceNetworkPlacement.Host,
        killSwitchEnabled: false,
        transientPassword);

    private static async Task<TcpClient> ConnectAdapterAsync(INetworkConnectionSession session)
    {
        var endpoint = session.Egress.ProxyEndpoint
            ?? throw new InvalidOperationException("The provider did not expose a proxy endpoint.");
        var client = new TcpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port);
        return client;
    }

    private static async Task NegotiateAdapterAsync(
        Stream stream,
        string targetHost,
        ushort targetPort)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        var greeting = new byte[2];
        await ReadRequiredAsync(stream, greeting);
        Assert.Equal(new byte[] { 5, 0 }, greeting);
        var host = Encoding.ASCII.GetBytes(targetHost);
        var request = new byte[7 + host.Length];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = 3;
        request[4] = (byte)host.Length;
        host.CopyTo(request, 5);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(5 + host.Length), targetPort);
        await stream.WriteAsync(request);
        var response = new byte[10];
        await ReadRequiredAsync(stream, response);
        Assert.Equal((byte)0, response[1]);
    }

    private static async Task<(string Host, int Port)> ServeSocksUpstreamAsync(
        TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var greeting = new byte[3];
        await ReadRequiredAsync(stream, greeting);
        Assert.Equal(new byte[] { 5, 1, 0 }, greeting);
        await stream.WriteAsync(new byte[] { 5, 0 });
        var request = new byte[5];
        await ReadRequiredAsync(stream, request);
        Assert.Equal((byte)3, request[3]);
        var host = new byte[request[4]];
        await ReadRequiredAsync(stream, host);
        var port = new byte[2];
        await ReadRequiredAsync(stream, port);
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        var payload = new byte[4];
        await ReadRequiredAsync(stream, payload);
        Assert.Equal("ping", Encoding.ASCII.GetString(payload));
        await stream.WriteAsync("pong"u8.ToArray());
        return (Encoding.ASCII.GetString(host), BinaryPrimitives.ReadUInt16BigEndian(port));
    }

    private static async Task<string> ServeHttpUpstreamAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var bytes = new List<byte>();
        while (bytes.Count < 16 * 1024)
        {
            var next = new byte[1];
            await ReadRequiredAsync(stream, next);
            bytes.Add(next[0]);
            if (bytes.Count >= 4
                && bytes[^4] == (byte)'\r'
                && bytes[^3] == (byte)'\n'
                && bytes[^2] == (byte)'\r'
                && bytes[^1] == (byte)'\n')
            {
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
                return Encoding.ASCII.GetString([.. bytes]);
            }
        }

        throw new InvalidOperationException("The HTTP CONNECT request was too large.");
    }

    private static async Task ReadRequiredAsync(Stream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..]);
            Assert.NotEqual(0, read);
            offset += read;
        }
    }

    private static T Success<T>(NetworkConnectionResult<T> result) =>
        Assert.IsType<NetworkConnectionResult<T>.Success>(result).Value;

    private static T Success<T>(SecretVaultResult<T> result) =>
        Assert.IsType<SecretVaultResult<T>.Success>(result).Value;

    private sealed class RecordingSocksReachabilityProbe(bool reachable) :
        ISocksReachabilityProbe
    {
        public List<int> Ports { get; } = [];

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WaitForCancellation { get; init; }

        public async ValueTask<SocksReachabilityResult> ProbeAsync(
            int socksPort,
            CancellationToken cancellationToken)
        {
            Ports.Add(socksPort);
            Started.TrySetResult();
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return reachable
                ? SocksReachabilityResult.Reachable
                : new SocksReachabilityResult(SocksReachabilityFailure.TransportFailed);
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value) => Items.Add(value);
    }
}
