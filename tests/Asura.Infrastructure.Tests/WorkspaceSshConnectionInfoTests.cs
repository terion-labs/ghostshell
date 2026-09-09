using System.Net;
using System.Net.Sockets;
using System.Text;
using Asura.Application;
using Asura.Core;
using Renci.SshNet;

namespace Asura.Infrastructure.Tests;

public sealed class WorkspaceSshConnectionInfoTests
{
    [Fact]
    public async Task First_host_key_scan_reaches_the_broker_with_the_unresolved_ssh_hostname()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestedHost = ReadProxyRequestAsync(listener, timeout.Token);
        var scanner = new SshNetHostKeyScanner(new Connector(port));
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("vpn-only.invalid", 2222, "user"));

        var result = await scanner.ScanAsync(profile, timeout.Token);

        Assert.IsType<ConnectionRuntimeResult<SshHostKeyCandidate>.Failure>(result);
        Assert.Equal("vpn-only.invalid", await requestedHost);
    }

    private static async Task<string> ReadProxyRequestAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, cancellationToken);
        await stream.ReadExactlyAsync(new byte[greeting[1]], cancellationToken);
        await stream.WriteAsync(new byte[] { 5, 2 }, cancellationToken);
        var authentication = new byte[2];
        await stream.ReadExactlyAsync(authentication, cancellationToken);
        await stream.ReadExactlyAsync(new byte[authentication[1]], cancellationToken);
        var passwordLength = new byte[1];
        await stream.ReadExactlyAsync(passwordLength, cancellationToken);
        await stream.ReadExactlyAsync(new byte[passwordLength[0]], cancellationToken);
        await stream.WriteAsync(new byte[] { 1, 0 }, cancellationToken);
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken);
        Assert.Equal(3, header[3]);
        var host = new byte[header[4]];
        await stream.ReadExactlyAsync(host, cancellationToken);
        await stream.ReadExactlyAsync(new byte[2], cancellationToken);
        await stream.WriteAsync(new byte[] { 5, 2, 0, 1, 0, 0, 0, 0, 0, 0 }, cancellationToken);
        return Encoding.ASCII.GetString(host);
    }

    [Fact]
    public void Security_probes_use_the_authenticated_workspace_broker_and_logical_ssh_host()
    {
        using var authentication = new NoneAuthenticationMethod("user");
        var connection = WorkspaceSshConnectionInfo.Create(
            new ConnectionEndpoint.Ssh("vpn-only.invalid", 2222, "user"),
            "user", authentication, new Connector());
        Assert.Equal("vpn-only.invalid", connection.Host);
        Assert.Equal(2222, connection.Port);
        Assert.Equal(ProxyTypes.Socks5, connection.ProxyType);
        Assert.Equal("127.0.0.1", connection.ProxyHost);
        Assert.Equal(45123, connection.ProxyPort);
        Assert.Equal("workspace", connection.ProxyUsername);
        Assert.Equal("credential", connection.ProxyPassword);
    }

    private sealed class Connector(int port = 45123) : IWorkspaceNetworkConnector
    {
        public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Blocked;
        public Uri LocalProxyEndpoint => new($"socks5://127.0.0.1:{port}");
        public WorkspaceNetworkProxyCredentials LocalProxyCredentials => new("workspace", "credential");
        public ValueTask<Stream> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
