using Asura.Application;

namespace Asura.Desktop;

/// <summary>
/// Dial-only authority for one broker generation. Its private SOCKS credentials
/// stop authenticating when the broker changes route, even if an open races Apply.
/// </summary>
internal sealed class WorkspaceNetworkRouteSnapshot : IWorkspaceNetworkConnector
{
    public WorkspaceNetworkRouteSnapshot(
        Uri proxyEndpoint,
        WorkspaceNetworkEgress egress,
        WorkspaceNetworkProxyCredentials credentials,
        CancellationToken routeLifetime)
    {
        LocalProxyEndpoint = proxyEndpoint;
        Egress = egress;
        LocalProxyCredentials = new WorkspaceNetworkProxyCredentials(credentials.Username, credentials.Password);
        RouteLifetime = routeLifetime;
    }

    public WorkspaceNetworkEgress Egress { get; }
    public Uri LocalProxyEndpoint { get; }
    public WorkspaceNetworkProxyCredentials LocalProxyCredentials { get; }
    public CancellationToken RouteLifetime { get; }

    public async ValueTask<Stream> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(RouteLifetime, cancellationToken);
        lifetime.Token.ThrowIfCancellationRequested();
        var stream = await WorkspaceSocksClient.ConnectAsync(
            LocalProxyEndpoint.Port, LocalProxyCredentials, host, port, lifetime.Token).ConfigureAwait(false);
        if (lifetime.IsCancellationRequested)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
        }
        return stream;
    }
}
