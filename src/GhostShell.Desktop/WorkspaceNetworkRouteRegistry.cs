using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

internal sealed class WorkspaceNetworkRouteRegistry : IWorkspaceNetworkRouteResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkspaceInstanceId, Route> _routes = [];

    public IDisposable Register(
        WorkspaceInstanceId workspaceId,
        IWorkspaceNetworkConnector connector,
        IConnectionCommandRuntime? isolatedCommandRuntime)
    {
        ArgumentNullException.ThrowIfNull(connector);
        var route = new Route(connector, isolatedCommandRuntime);
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
        IConnectionCommandRuntime? IsolatedCommandRuntime);

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
