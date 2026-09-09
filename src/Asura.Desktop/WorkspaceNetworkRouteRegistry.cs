using Asura.Application;
using Asura.ConnectionBackend;
using Asura.Core;

namespace Asura.Desktop;

internal sealed class WorkspaceNetworkRouteRegistry : IWorkspaceNetworkRouteResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkspaceInstanceId, Route> _routes = [];

    public IDisposable Register(
        WorkspaceInstanceId workspaceId,
        IWorkspaceNetworkConnector connector,
        IConnectionCommandRuntime? isolatedCommandRuntime,
        WorkspaceConnectionBackendFactory.Session? backend = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        var route = new Route(connector, isolatedCommandRuntime, backend);
        lock (_gate)
        {
            if (!_routes.TryAdd(workspaceId, route))
            {
                throw new InvalidOperationException(
                    "The workspace already has a network consumer route.");
            }
        }

        return new Registration(this, workspaceId, route);
    }

    public IWorkspaceNetworkConnector? ConnectorFor(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            return _routes.GetValueOrDefault(workspaceId)?.Connector;
        }
    }

    public HttpMessageHandler CreateHttpHandler(Uri proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        lock (_gate)
        {
            foreach (var route in _routes.Values)
            {
                var expected = new UriBuilder(route.Connector.LocalProxyEndpoint);
                if (route.Connector.LocalProxyCredentials is { } credentials)
                {
                    expected.UserName = credentials.Username;
                    expected.Password = credentials.Password;
                }
                if (string.Equals(expected.Uri.AbsoluteUri, proxy.AbsoluteUri, StringComparison.Ordinal)
                    && route.Backend is { } backend)
                {
                    return new WorkspaceHttpMessageHandler(token => backend.PlanAsync("http", null, token));
                }
            }
        }
        throw new InvalidOperationException("The workspace HTTP route is no longer registered. No host-network fallback was attempted.");
    }

    public IConnectionCommandRuntime? IsolatedCommandRuntimeFor(
        WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            return _routes.GetValueOrDefault(workspaceId)?.IsolatedCommandRuntime;
        }
    }

    private void Unregister(WorkspaceInstanceId workspaceId, Route route)
    {
        lock (_gate)
        {
            if (_routes.TryGetValue(workspaceId, out var current)
                && ReferenceEquals(current, route))
            {
                _routes.Remove(workspaceId);
            }
        }
    }

    private sealed record Route(
        IWorkspaceNetworkConnector Connector,
        IConnectionCommandRuntime? IsolatedCommandRuntime,
        WorkspaceConnectionBackendFactory.Session? Backend);

    private sealed class Registration(
        WorkspaceNetworkRouteRegistry owner,
        WorkspaceInstanceId workspaceId,
        Route route) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unregister(workspaceId, route);
            }
        }
    }
}
