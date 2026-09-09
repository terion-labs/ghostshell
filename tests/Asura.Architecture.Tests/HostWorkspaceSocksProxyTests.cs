using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Asura.App;
using Asura.Application;
using Asura.Core;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class HostWorkspaceSocksProxyTests
{
    [Fact]
    public async Task Changing_browser_auth_authority_blocks_until_caches_are_cleared_but_reconnect_preserves_it()
    {
        await using var proxy = new HostWorkspaceSocksProxy();
        var clear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        proxy.BrowserAuthenticationRouteChanging += cancellationToken =>
        {
            calls++;
            return clear.Task.WaitAsync(cancellationToken);
        };
        var route = WorkspaceNetworkEgress.ViaProxy(new Uri("socks5://127.0.0.1:41001"));
        proxy.Apply(route, "network-one");
        Assert.Equal(WorkspaceNetworkEgress.Blocked, proxy.Egress);
        Assert.Null(proxy.BrowserAuthenticationRouteIdentity);
        Assert.Equal(1, calls);

        clear.SetResult();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (proxy.Egress == WorkspaceNetworkEgress.Blocked)
        {
            await Task.Delay(10, deadline.Token);
        }
        Assert.Equal("network-one", proxy.BrowserAuthenticationRouteIdentity);
        proxy.Apply(WorkspaceNetworkEgress.Blocked, null);
        proxy.Apply(route, "network-one");
        Assert.Equal(route, proxy.Egress);
        Assert.Equal(1, calls);

        proxy.Apply(WorkspaceNetworkEgress.Direct, "local");
        Assert.Equal(2, calls);
        Assert.Equal("local", proxy.BrowserAuthenticationRouteIdentity);
    }

    [Fact]
    public async Task Failed_browser_auth_reset_keeps_traffic_blocked_and_can_retry()
    {
        await using var proxy = new HostWorkspaceSocksProxy();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.BrowserAuthenticationRouteFailed += (_, _) => failed.TrySetResult();
        Func<CancellationToken, Task> reject = _ => Task.FromException(new IOException("synthetic reset failure"));
        proxy.BrowserAuthenticationRouteChanging += reject;
        proxy.Apply(WorkspaceNetworkEgress.Attached, "network-one");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(WorkspaceNetworkEgress.Blocked, proxy.Egress);
        Assert.Null(proxy.BrowserAuthenticationRouteIdentity);

        proxy.BrowserAuthenticationRouteChanging -= reject;
        proxy.Apply(WorkspaceNetworkEgress.Attached, "network-one");
        Assert.Equal(WorkspaceNetworkEgress.Attached, proxy.Egress);
        Assert.Equal("network-one", proxy.BrowserAuthenticationRouteIdentity);
    }

    [Fact]
    public async Task Browser_storage_identity_survives_recreating_the_host_broker()
    {
        await using var first = new HostWorkspaceSocksProxy("workspace:stable");
        await using var second = new HostWorkspaceSocksProxy("workspace:stable");
        Assert.NotEqual(first.LocalProxyEndpoint, second.LocalProxyEndpoint);
        Assert.Equal("workspace:stable", first.BrowserProfileRouteIdentity);
        Assert.Equal(first.BrowserProfileRouteIdentity, second.BrowserProfileRouteIdentity);
    }

    [Fact]
    public async Task Direct_route_reaches_the_destination()
    {
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        var echo = EchoOnceAsync(destination);
        await using var proxy = new HostWorkspaceSocksProxy();
        using var client = await OpenAsync(proxy, "127.0.0.1", destinationPort);

        await client.GetStream().WriteAsync("ping"u8.ToArray());
        var reply = new byte[4];
        await ReadRequiredAsync(client.GetStream(), reply);

        Assert.Equal("pong", Encoding.ASCII.GetString(reply));
        await echo;
    }

    [Fact]
    public async Task Proxy_route_chains_through_the_selected_adapter()
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var observed = ObserveSocksDestinationAsync(upstream);
        await using var proxy = new HostWorkspaceSocksProxy();
        proxy.Apply(WorkspaceNetworkEgress.ViaProxy(
            new Uri($"socks5://127.0.0.1:{upstreamPort}")));

        using var client = await OpenAsync(proxy, "service.example", 9443);

        Assert.Equal(("service.example", 9443), await observed);
    }

    [Fact]
    public async Task Blocked_route_rejects_new_connections()
    {
        await using var proxy = new HostWorkspaceSocksProxy();
        proxy.Apply(WorkspaceNetworkEgress.Blocked);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort);
        var stream = client.GetStream();
        await AuthenticateAsync(stream, proxy.LocalProxyCredentials);
        await stream.WriteAsync(new byte[] { 5, 1, 0, 3, 1, (byte)'x', 0, 80 });
        var response = new byte[10];
        await ReadRequiredAsync(stream, response);

        Assert.Equal((byte)2, response[1]);
    }

    [Fact]
    public async Task Unauthenticated_socks_client_cannot_borrow_workspace_route()
    {
        await using var proxy = new HostWorkspaceSocksProxy();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        var response = new byte[2];
        await ReadRequiredAsync(stream, response);

        Assert.Equal(new byte[] { 5, 255 }, response);
    }

    [Fact]
    public async Task Another_workspaces_socks_credentials_are_rejected()
    {
        await using var owner = new HostWorkspaceSocksProxy();
        await using var other = new HostWorkspaceSocksProxy();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, owner.LocalPort);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 });
        var greeting = new byte[2];
        await ReadRequiredAsync(stream, greeting);
        Assert.Equal(new byte[] { 5, 2 }, greeting);
        var username = Encoding.UTF8.GetBytes(other.LocalProxyCredentials.Username);
        var password = Encoding.UTF8.GetBytes(other.LocalProxyCredentials.Password);
        byte[] authentication =
        [
            1,
            checked((byte)username.Length),
            .. username,
            checked((byte)password.Length),
            .. password,
        ];
        await stream.WriteAsync(authentication);
        var response = new byte[2];
        await ReadRequiredAsync(stream, response);

        Assert.Equal(new byte[] { 1, 1 }, response);
    }

    [Fact]
    public async Task Http_connect_requires_workspace_credentials()
    {
        await using var proxy = new HostWorkspaceSocksProxy();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort);
        await client.GetStream().WriteAsync(
            "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n"u8.ToArray());
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);

        Assert.Equal(
            "HTTP/1.1 407 Proxy Authentication Required",
            await reader.ReadLineAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticated_http_connect_reaches_destination(bool credentialedUpstream)
    {
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        var echo = EchoOnceAsync(destination);
        await using var upstream = new HostWorkspaceSocksProxy();
        await using var proxy = new HostWorkspaceSocksProxy(
            upstreamCredentials: credentialedUpstream ? upstream.LocalProxyCredentials : null);
        if (credentialedUpstream)
        {
            proxy.Apply(WorkspaceNetworkEgress.ViaProxy(upstream.LocalProxyEndpoint));
            Assert.NotEqual(upstream.LocalProxyCredentials.Password, proxy.LocalProxyCredentials.Password, StringComparer.Ordinal);
        }
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{proxy.LocalProxyCredentials.Username}:{proxy.LocalProxyCredentials.Password}"));
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
            $"CONNECT 127.0.0.1:{destinationPort} HTTP/1.1\r\n"
            + $"Host: 127.0.0.1:{destinationPort}\r\n"
            + $"Proxy-Authorization: Basic {token}\r\n\r\n"));
        using var reader = new StreamReader(
            client.GetStream(),
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        Assert.Equal("HTTP/1.1 200 Connection Established", await reader.ReadLineAsync());
        Assert.Equal(string.Empty, await reader.ReadLineAsync());

        await client.GetStream().WriteAsync("ping"u8.ToArray());
        var reply = new byte[4];
        await ReadRequiredAsync(client.GetStream(), reply);

        Assert.Equal("pong", Encoding.ASCII.GetString(reply));
        await echo;
    }

    [Fact]
    public async Task Inner_proxy_credentials_do_not_authorize_the_outer_http_listener()
    {
        await using var upstream = new HostWorkspaceSocksProxy();
        await using var proxy = new HostWorkspaceSocksProxy(upstreamCredentials: upstream.LocalProxyCredentials);
        proxy.Apply(WorkspaceNetworkEgress.ViaProxy(upstream.LocalProxyEndpoint));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{upstream.LocalProxyCredentials.Username}:{upstream.LocalProxyCredentials.Password}"));
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
            "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n"
            + $"Proxy-Authorization: Basic {token}\r\n\r\n"));
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("HTTP/1.1 407 Proxy Authentication Required", await reader.ReadLineAsync(timeout.Token));
    }

    [Fact]
    public async Task Stop_immediately_closes_listener_before_async_drain()
    {
        await using var proxy = new HostWorkspaceSocksProxy();
        proxy.Stop();
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<SocketException>(async () =>
            await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort, timeout.Token));
    }

    [Fact]
    public async Task Authenticated_http_request_is_forwarded_without_proxy_credentials()
    {
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        var observed = ObserveHttpRequestAsync(destination);
        await using var proxy = new HostWorkspaceSocksProxy();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{proxy.LocalProxyCredentials.Username}:{proxy.LocalProxyCredentials.Password}"));
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
            $"GET http://127.0.0.1:{destinationPort}/status?full=1 HTTP/1.1\r\n"
            + $"Host: 127.0.0.1:{destinationPort}\r\n"
            + "Connection: keep-alive\r\n"
            + "Proxy-Connection: keep-alive\r\n"
            + $"Proxy-Authorization: Basic {token}\r\n\r\n"));
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);

        Assert.Equal("HTTP/1.1 204 No Content", await reader.ReadLineAsync());
        var forwarded = await observed;
        Assert.StartsWith(
            "GET /status?full=1 HTTP/1.1\r\n",
            forwarded,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Authorization", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Proxy-Connection", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keep-alive", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection: close\r\n", forwarded, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_response_survives_a_fin_sensitive_upstream_forward(bool isolated)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var observed = ServeFinSensitiveHttpResponseAsync(upstream, deadline.Token);
        await using IAsyncDisposable proxy = isolated
            ? new WorkspaceIsolationSocksProxy(new RejectingCommandRuntime(), BuiltInConnections.Local)
            : new HostWorkspaceSocksProxy();
        var connector = (IWorkspaceNetworkConnector)proxy;
        var credentials = Assert.IsType<WorkspaceNetworkProxyCredentials>(connector.LocalProxyCredentials);
        ((IWorkspaceNetworkEgressSink)proxy).Apply(WorkspaceNetworkEgress.ViaProxy(
            new Uri($"socks5://127.0.0.1:{upstreamPort}")));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, connector.LocalProxyEndpoint.Port, deadline.Token);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{credentials.Username}:{credentials.Password}"));
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
            "GET http://origin.invalid:8080/delayed HTTP/1.1\r\n"
            + "Host: origin.invalid:8080\r\n"
            + $"Proxy-Authorization: Basic {token}\r\n\r\n"
            + "GET http://other.invalid/second HTTP/1.1\r\n"
            + $"Proxy-Authorization: Basic {token}\r\n\r\n"), deadline.Token);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
        var response = await reader.ReadToEndAsync(deadline.Token);
        var forwarded = await observed;

        Assert.EndsWith("\r\n\r\ncomplete", response, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", response, StringComparison.Ordinal);
        Assert.StartsWith("GET /delayed HTTP/1.1\r\n", forwarded, StringComparison.Ordinal);
        Assert.Contains("Connection: close\r\n", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Proxy-Authorization", forwarded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Workspace_proxy_credentials_are_unique_and_redacted()
    {
        await using var first = new HostWorkspaceSocksProxy();
        await using var second = new HostWorkspaceSocksProxy();

        Assert.NotEqual(
            first.LocalProxyCredentials.Password,
            second.LocalProxyCredentials.Password,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            first.LocalProxyCredentials.Password,
            first.LocalProxyCredentials.ToString(),
            StringComparison.Ordinal);
    }


    private static async Task<TcpClient> OpenAsync(
        HostWorkspaceSocksProxy proxy,
        string host,
        int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort, timeout.Token);
        var stream = client.GetStream();
        await AuthenticateAsync(stream, proxy.LocalProxyCredentials, timeout.Token);
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var request = new byte[7 + hostBytes.Length];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = 3;
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        BinaryPrimitives.WriteUInt16BigEndian(
            request.AsSpan(5 + hostBytes.Length),
            checked((ushort)port));
        await stream.WriteAsync(request, timeout.Token);
        var response = new byte[10];
        await ReadRequiredAsync(stream, response, timeout.Token);
        Assert.Equal((byte)0, response[1]);
        return client;
    }

    private static async Task AuthenticateAsync(
        Stream stream,
        WorkspaceNetworkProxyCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 2 }, cancellationToken);
        var greeting = new byte[2];
        await ReadRequiredAsync(stream, greeting, cancellationToken);
        Assert.Equal(new byte[] { 5, 2 }, greeting);
        var username = Encoding.UTF8.GetBytes(credentials.Username);
        var password = Encoding.UTF8.GetBytes(credentials.Password);
        var request = new byte[3 + username.Length + password.Length];
        request[0] = 1;
        request[1] = checked((byte)username.Length);
        username.CopyTo(request, 2);
        request[2 + username.Length] = checked((byte)password.Length);
        password.CopyTo(request, 3 + username.Length);
        await stream.WriteAsync(request, cancellationToken);
        var response = new byte[2];
        await ReadRequiredAsync(stream, response, cancellationToken);
        Assert.Equal(new byte[] { 1, 0 }, response);
    }

    private static async Task EchoOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var request = new byte[4];
        await ReadRequiredAsync(client.GetStream(), request);
        Assert.Equal("ping", Encoding.ASCII.GetString(request));
        await client.GetStream().WriteAsync("pong"u8.ToArray());
    }

    private static async Task<(string Host, int Port)> ObserveSocksDestinationAsync(
        TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var greeting = new byte[3];
        await ReadRequiredAsync(stream, greeting);
        await stream.WriteAsync(new byte[] { 5, 0 });
        var request = new byte[5];
        await ReadRequiredAsync(stream, request);
        var host = new byte[request[4]];
        await ReadRequiredAsync(stream, host);
        var port = new byte[2];
        await ReadRequiredAsync(stream, port);
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        return (Encoding.ASCII.GetString(host), BinaryPrimitives.ReadUInt16BigEndian(port));
    }

    private static async Task<string> ObserveHttpRequestAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var request = await ReadHttpHeadersAsync(stream, CancellationToken.None);
        await stream.WriteAsync(
            "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray());
        return request;
    }

    private static async Task<string> ServeFinSensitiveHttpResponseAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var greeting = new byte[3];
        await ReadRequiredAsync(stream, greeting, cancellationToken);
        Assert.Equal(new byte[] { 5, 1, 0 }, greeting);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken);
        var request = new byte[5];
        await ReadRequiredAsync(stream, request, cancellationToken);
        var destination = new byte[request[4] + 2];
        await ReadRequiredAsync(stream, destination, cancellationToken);
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, cancellationToken);
        var headers = await ReadHttpHeadersAsync(stream, cancellationToken);

        // SSH.NET ends its forwarding channel when the broker sends FIN. Model
        // that contract while the origin has not produced its response yet.
        // This is a bounded EOF observation, not a sleep before an assertion.
        using var responseDelay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseDelay.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            Assert.Equal(0, await stream.ReadAsync(new byte[1], responseDelay.Token));
            return headers;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        await stream.WriteAsync(
            "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 8\r\n\r\ncomplete"u8.ToArray(),
            cancellationToken);
        return headers;
    }

    private static async Task<string> ReadHttpHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 4096)
        {
            var next = new byte[1];
            await ReadRequiredAsync(stream, next, cancellationToken);
            bytes.Add(next[0]);
            if (bytes.Count >= 4
                && bytes[^4] == '\r'
                && bytes[^3] == '\n'
                && bytes[^2] == '\r'
                && bytes[^1] == '\n')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString([.. bytes]);
    }

    private static Task ReadRequiredAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        stream.ReadExactlyAsync(buffer, cancellationToken).AsTask();

    private sealed class RejectingCommandRuntime : IConnectionCommandRuntime
    {
        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
            ConnectionProfile connection, string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No isolate command may run in this test.");

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(
            ConnectionProfile connection, string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No isolate command may run in this test.");
    }

}
