using GhostShell.Core;

namespace GhostShell.Desktop;

internal sealed class WorkspaceSessionFactoryRegistry<TFactory>(
    TFactory hostFactory,
    string duplicateRegistrationMessage)
    where TFactory : class
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkspaceInstanceId, TFactory> _factories = [];

    public IDisposable Register(WorkspaceInstanceId workspaceId, TFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            if (!_factories.TryAdd(workspaceId, factory))
            {
                throw new InvalidOperationException(duplicateRegistrationMessage);
            }
        }

        return new Registration(this, workspaceId, factory);
    }

    public TFactory Resolve(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            return _factories.GetValueOrDefault(workspaceId, hostFactory);
        }
    }

    private void Unregister(WorkspaceInstanceId workspaceId, TFactory factory)
    {
        lock (_gate)
        {
            if (_factories.TryGetValue(workspaceId, out var current)
                && ReferenceEquals(current, factory))
            {
                _factories.Remove(workspaceId);
            }
        }
    }

    private sealed class Registration(
        WorkspaceSessionFactoryRegistry<TFactory> owner,
        WorkspaceInstanceId workspaceId,
        TFactory factory) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unregister(workspaceId, factory);
            }
        }
    }
}
