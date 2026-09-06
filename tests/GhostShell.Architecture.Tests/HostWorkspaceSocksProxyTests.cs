using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class HostWorkspaceSocksProxyTests
{
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

    [Fact]
    public async Task Authenticated_http_connect_reaches_destination()
    {
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        var echo = EchoOnceAsync(destination);
        await using var proxy = new HostWorkspaceSocksProxy();
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

    [Fact]
    public async Task Database_relay_opens_its_real_socket_through_workspace_connector()
    {
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        var echo = EchoOnceAsync(destination);
        await using var proxy = new HostWorkspaceSocksProxy();
        var tunnels = new WorkspaceNetworkDatabaseTunnelFactory(
            proxy,
            new RejectingSshTunnelFactory());
        await using var tunnel = await tunnels.OpenAsync(
            BuiltInConnections.Local,
            "127.0.0.1",
            destinationPort,
            CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort);

        await client.GetStream().WriteAsync("ping"u8.ToArray());
        var reply = new byte[4];
        await ReadRequiredAsync(client.GetStream(), reply);

        Assert.Equal("pong", Encoding.ASCII.GetString(reply));
        client.Dispose();
        await echo;
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
        var bytes = new List<byte>();
        while (bytes.Count < 4096)
        {
            var next = new byte[1];
            await ReadRequiredAsync(stream, next);
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

        await stream.WriteAsync(
            "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray());
        return Encoding.ASCII.GetString([.. bytes]);
    }

    private static Task ReadRequiredAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        stream.ReadExactlyAsync(buffer, cancellationToken).AsTask();

    private sealed class RejectingSshTunnelFactory : IDatabaseTunnelFactory
    {
        public ValueTask<IDatabaseTunnelLease> OpenAsync(
            ConnectionProfile connection,
            string targetHost,
            int targetPort,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IDatabaseTunnelLease>(
                new InvalidOperationException("The test does not use an SSH tunnel."));
    }
}
