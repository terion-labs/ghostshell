using GhostShell.App;
using GhostShell.Application;
using GhostShell.Browser;
using GhostShell.Core;
using GhostShell.Files;

namespace GhostShell.Desktop;

internal sealed class DesktopBrowserRendererViewFactory(
    BrowserPanelSessionFactory sessionFactory,
    SshNetBrowserTunnelFactory tunnelFactory,
    CefBrowserProfileStore profileStore) : IBrowserRendererViewFactory, IDisposable, IAsyncDisposable
{
    private readonly object _routeGate = new();
    private readonly Dictionary<RemoteRouteKey, RemoteRoute> _remoteRoutes = [];
    private readonly Dictionary<RemoteRoute, Task> _routeDrains = [];
    private bool _disposed;

    public BrowserRendererView Create()
    {
        var profile = profileStore.AcquireLocal(BrowserProfileKey.Global);
        return CreateView(profile);
    }

    public BrowserRendererView CreateIsolatedHtmlPreview()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var surface = BrowserSurface.CreateIsolatedHtmlPreview(
            sessionFactory.CapabilityProfile);
        return new BrowserRendererView(
            surface,
            surface,
            surface,
            surface.SetAgentActivity,
            surface.OpenDeveloperTools);
    }

    public ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken) => CreateAsync(
            connection,
            BrowserProfileKey.Global,
            cancellationToken);

    public ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileKey profile,
        CancellationToken cancellationToken) => CreateAsync(
            connection,
            BrowserProfileBinding.Legacy(profile),
            cancellationToken);

    public async ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileBinding profile,
        CancellationToken cancellationToken)
    {
        return await CreateRoutedAsync(
                connection,
                profile,
                networkConnector: null,
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileBinding profile,
        IWorkspaceNetworkConnector networkConnector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(networkConnector);
        return await CreateRoutedAsync(
                connection,
                profile,
                networkConnector,
                cancellationToken)
            .ConfigureAwait(true);
    }

    private async ValueTask<BrowserRendererView> CreateRoutedAsync(
        ConnectionProfile connection,
        BrowserProfileBinding profile,
        IWorkspaceNetworkConnector? networkConnector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Endpoint is ConnectionEndpoint.Local)
        {
            var localLease = networkConnector is null
                ? profileStore.AcquireLocal(profile)
                : profileStore.AcquireRouted(
                    profile,
                    networkConnector.LocalProxyEndpoint.AbsoluteUri,
                    networkConnector);
            try
            {
                await localLease.Ready.WaitAsync(cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                return CreateView(localLease);
            }
            catch
            {
                localLease.Dispose();
                throw;
            }
        }

        if (connection.Endpoint is not ConnectionEndpoint.Ssh)
        {
            throw new InvalidOperationException(
                $"{connection.ConnectionKind} connections cannot route a browser.");
        }

        var route = await AcquireRemoteRouteAsync(
            profile,
            connection,
            networkConnector,
            cancellationToken).ConfigureAwait(true);
        CefBrowserProfileLease? profileLease = null;
        try
        {
            profileLease = profileStore.AcquireRouted(
                profile,
                route.Tunnel.ProfileRouteIdentity,
                route.Proxy,
                BrowserHttpAuthentication.SshRouteIdentity(connection));
            await profileLease.Ready.WaitAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            var surface = new BrowserSurface(
                sessionFactory.CapabilityProfile,
                profileLease);
            profileLease = null;
            return new BrowserRendererView(
                surface,
                surface,
                new RoutedBrowserLifetime(
                    surface,
                    () => ReleaseRemoteRoute(route)),
                surface.SetAgentActivity,
                surface.OpenDeveloperTools);
        }
        catch
        {
            profileLease?.Dispose();
            ReleaseRemoteRoute(route);
            throw;
        }
    }

    public async ValueTask<BrowserRendererView> CreateThroughSocksProxyAsync(
        int socksProxyPort,
        string routeIdentity,
        BrowserProfileBinding profile,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(socksProxyPort, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        var lease = profileStore.AcquireRouted(
            profile,
            routeIdentity,
            socksProxyPort);
        try
        {
            await lease.Ready.WaitAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            return CreateView(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        RemoteRoute[] routes;
        lock (_routeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            routes = [.. _remoteRoutes.Values];
            _remoteRoutes.Clear();
        }

        foreach (var route in routes)
        {
            DisposeRoute(route);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task[] drains;
        lock (_routeGate)
        {
            drains = [.. _routeDrains.Values];
        }

        await Task.WhenAll(drains).ConfigureAwait(false);
    }

    private void DisposeRoute(RemoteRoute route)
    {
        lock (_routeGate)
        {
            route.Proxy.Stop();
            route.Tunnel.Dispose();
            var drain = DrainRouteAsync(route);
            if (!drain.IsCompleted)
            {
                _routeDrains[route] = drain;
            }
        }
    }

    private async Task DrainRouteAsync(RemoteRoute route)
    {
        try
        {
            await route.Proxy.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "browser.ssh-proxy.dispose-failed", SecretSafeDiagnosticKind.Unexpected);
        }
        finally
        {
            lock (_routeGate)
            {
                _routeDrains.Remove(route);
            }
        }
    }

    private BrowserRendererView CreateView(CefBrowserProfileLease profile)
    {
        try
        {
            var surface = new BrowserSurface(
                sessionFactory.CapabilityProfile,
                profile);
            return new BrowserRendererView(
                surface,
                surface,
                surface,
                surface.SetAgentActivity,
                surface.OpenDeveloperTools);
        }
        catch
        {
            profile.Dispose();
            throw;
        }
    }

    private async ValueTask<RemoteRoute> AcquireRemoteRouteAsync(
        BrowserProfileBinding profile,
        ConnectionProfile connection,
        IWorkspaceNetworkConnector? networkConnector,
        CancellationToken cancellationToken)
    {
        var key = CreateRemoteRouteKey(profile.Selection, connection, networkConnector);
        lock (_routeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_remoteRoutes.TryGetValue(key, out var existing))
            {
                existing.ActiveBrowsers++;
                return existing;
            }
        }

        var tunnel = await tunnelFactory
            .OpenAsync(connection, networkConnector, cancellationToken)
            .ConfigureAwait(true);
        lock (_routeGate)
        {
            if (_disposed)
            {
                tunnel.Dispose();
                throw new ObjectDisposedException(nameof(DesktopBrowserRendererViewFactory));
            }

            if (_remoteRoutes.TryGetValue(key, out var raced))
            {
                raced.ActiveBrowsers++;
                tunnel.Dispose();
                return raced;
            }

            RemoteRoute created;
            try
            {
                created = new RemoteRoute(key, tunnel) { ActiveBrowsers = 1 };
            }
            catch
            {
                tunnel.Dispose();
                throw;
            }
            _remoteRoutes.Add(key, created);
            return created;
        }
    }

    private void ReleaseRemoteRoute(RemoteRoute route)
    {
        lock (_routeGate)
        {
            if (route.ActiveBrowsers <= 0)
            {
                return;
            }

            route.ActiveBrowsers--;
            if (route.ActiveBrowsers == 0)
            {
                _remoteRoutes.Remove(route.Key);
                DisposeRoute(route);
            }
        }
    }

    private sealed class RoutedBrowserLifetime(
        BrowserSurface surface,
        Action releaseRoute) : IDisposable
    {
        private Action? _releaseRoute = releaseRoute;

        public void Dispose()
        {
            try
            {
                surface.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref _releaseRoute, null)?.Invoke();
            }
        }
    }

    internal static RemoteRouteKey CreateRemoteRouteKey(
        BrowserProfileSelection profile,
        ConnectionProfile connection,
        IWorkspaceNetworkConnector? networkConnector) => new(
            profile,
            connection.Id,
            connection.Endpoint,
            connection.Authentication,
            connection.HostKeyPolicy,
            networkConnector?.LocalProxyEndpoint.AbsoluteUri);

    internal readonly record struct RemoteRouteKey(
        BrowserProfileSelection Profile,
        ConnectionId ConnectionId,
        ConnectionEndpoint Endpoint,
        ConnectionAuthentication Authentication,
        SshHostKeyPolicy HostKeyPolicy,
        string? NetworkRouteIdentity);

    private sealed class RemoteRoute
    {
        public RemoteRoute(RemoteRouteKey key, SshNetBrowserTunnelFactory.SshBrowserTunnel tunnel)
        {
            Key = key;
            Tunnel = tunnel;
            Proxy = new HostWorkspaceSocksProxy(tunnel.ProfileRouteIdentity, tunnel.ProxyCredentials);
            Proxy.Apply(WorkspaceNetworkEgress.ViaProxy(
                new Uri($"socks5://127.0.0.1:{tunnel.LocalPort}", UriKind.Absolute)));
        }

        public RemoteRouteKey Key { get; }

        public SshNetBrowserTunnelFactory.SshBrowserTunnel Tunnel { get; }

        public HostWorkspaceSocksProxy Proxy { get; }

        public int ActiveBrowsers { get; set; }
    }
}
