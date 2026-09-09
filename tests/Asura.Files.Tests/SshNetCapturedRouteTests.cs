using Asura.Application;
using Asura.Core;
using Renci.SshNet;

namespace Asura.Files.Tests;

public sealed class SshNetCapturedRouteTests
{
    [Fact]
    public void Ssh_connection_info_uses_only_the_supplied_frozen_route_credentials()
    {
        using var lifetime = new CancellationTokenSource();
        var route = new FrozenConnector(lifetime.Token);
        using var authentication = new NoneAuthenticationMethod("synthetic-user");
        var info = SshNetConnectionInfoFactory.Create(
            new ConnectionEndpoint.Ssh("ssh.synthetic.invalid", 2222, "synthetic-user"),
            "synthetic-user", authentication, route);
        Assert.Equal("ssh.synthetic.invalid", info.Host);
        Assert.Equal(2222, info.Port);
        Assert.Equal(ProxyTypes.Socks5, info.ProxyType);
        Assert.Equal("generation-user", info.ProxyUsername);
        Assert.Equal("generation-password", info.ProxyPassword);
        lifetime.Cancel();
        Assert.True(route.RouteLifetime.IsCancellationRequested);
        Assert.Equal("generation-password", info.ProxyPassword);
    }

    private sealed class FrozenConnector(CancellationToken lifetime) : IWorkspaceNetworkConnector
    {
        public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Direct;
        public Uri LocalProxyEndpoint => new("socks5://127.0.0.1:41001");
        public WorkspaceNetworkProxyCredentials LocalProxyCredentials { get; } = new("generation-user", "generation-password");
        public CancellationToken RouteLifetime => lifetime;
        public ValueTask<Stream> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
