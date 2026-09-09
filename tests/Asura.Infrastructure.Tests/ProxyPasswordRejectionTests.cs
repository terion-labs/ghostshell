using System.Net;
using System.Net.Sockets;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class ProxyPasswordRejectionTests
{
    [Theory]
    [InlineData("HTTP/1.1 407 Proxy Authentication Required", true)]
    [InlineData("HTTP/1.0 407 Proxy Authentication Required", true)]
    [InlineData("HTTP/1.1 403 Forbidden", false)]
    [InlineData("HTTP/1.1 401 Unauthorized", false)]
    [InlineData("HTTP/1.1 502 Bad Gateway", false)]
    [InlineData("HTTP/1.1 4070 Invalid", false)]
    public async Task Http_407_is_distinguished_from_transport_and_destination_errors(string status, bool rejected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var serving = ServeHttpAsync(upstream, status, deadline.Token);

        var error = await ConnectAsync(NetworkProxyProtocol.Http, upstream, deadline.Token);

        await serving;
        Assert.Equal(rejected ? NetworkConnectionErrorCode.AuthenticationRejected : NetworkConnectionErrorCode.ConnectionFailed, error.Code);
        Assert.Equal(!rejected, error.Retryable);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 255, true)]
    [InlineData(5, 1, false)]
    public async Task Socks_password_rejection_requires_a_valid_authentication_response(byte version, byte status, bool rejected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var serving = ServeSocksAsync(upstream, version, status, deadline.Token);

        var error = await ConnectAsync(NetworkProxyProtocol.Socks5, upstream, deadline.Token);

        await serving;
        Assert.Equal(rejected ? NetworkConnectionErrorCode.AuthenticationRejected : NetworkConnectionErrorCode.ConnectionFailed, error.Code);
    }

    private static async Task<NetworkConnectionError> ConnectAsync(NetworkProxyProtocol protocol, TcpListener upstream, CancellationToken cancellationToken)
    {
        using var vault = new InMemorySecretVault();
        using var password = SecretMaterial.CopyFrom("incorrect-test-password"u8);
        var provider = new ProxyNetworkConnectionProvider(vault, new WorkspaceTcpConnector(null), new LocalProbe());
        var profile = new NetworkConnectionProfile(new NetworkConnectionId("proxy-auth-test"),
            NetworkConnectionProfile.CurrentSchemaVersion, "Test proxy",
            new NetworkConnectionConfiguration.Proxy(protocol, "127.0.0.1", ((IPEndPoint)upstream.LocalEndpoint).Port, "alice"));
        var result = await provider.ConnectAsync(new NetworkConnectionStartRequest(new WorkspaceInstanceId("test"),
            profile, WorkspaceNetworkPlacement.Host, false, password), null, cancellationToken);
        var error = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(result).Error;
        Assert.DoesNotContain("incorrect-test-password", error.Message, StringComparison.Ordinal);
        return error;
    }

    private static async Task ServeHttpAsync(TcpListener listener, string status, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 }) { }
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{status}\r\nContent-Length: 0\r\n\r\n"), cancellationToken);
    }

    private static async Task ServeSocksAsync(TcpListener listener, byte version, byte status, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var greeting = new byte[4];
        await stream.ReadExactlyAsync(greeting, cancellationToken);
        await stream.WriteAsync(new byte[] { 5, 2 }, cancellationToken);
        var auth = new byte[2];
        await stream.ReadExactlyAsync(auth, cancellationToken);
        var usernameAndLength = new byte[auth[1] + 1];
        await stream.ReadExactlyAsync(usernameAndLength, cancellationToken);
        await stream.ReadExactlyAsync(new byte[usernameAndLength[^1]], cancellationToken);
        await stream.WriteAsync(new byte[] { version, status }, cancellationToken);
    }

    private sealed class LocalProbe : ISocksReachabilityProbe
    {
        public async ValueTask<SocksReachabilityResult> ProbeAsync(int socksPort, CancellationToken cancellationToken)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, socksPort, cancellationToken);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken);
            await stream.ReadExactlyAsync(new byte[2], cancellationToken);
            // The fake upstream receives this request; no public destination is contacted.
            await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 192, 0, 2, 1, 1, 187 }, cancellationToken);
            var response = new byte[10];
            await stream.ReadExactlyAsync(response, cancellationToken);
            Assert.NotEqual((byte)0, response[1]);
            return new SocksReachabilityResult(SocksReachabilityFailure.DestinationRejected);
        }
    }
}
