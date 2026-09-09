using System.Diagnostics;
using Asura.Application;
using Asura.ConnectionBackend;
using Asura.Infrastructure;

namespace Asura.Desktop;

/// <summary>
/// Owns Direct-only host child operations for one immutable route generation.
/// Cancellation closes their IPC owners; the scratch lease prevents premature
/// deletion while a child is still alive. There is no network-failure fallback.
/// </summary>
internal sealed class HostConnectionBackend : IAsyncDisposable
{
    private readonly SelfReentryLaunch _launch;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _gate = new();
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private bool _disposed;

    internal HostConnectionBackend(SelfReentryLaunch launch, Func<DatabaseValueContentStore> stores,
        CancellationToken route, CancellationToken owner)
    {
        _launch = launch;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(route, owner);
        Lifetime = _lifetime.Token;
        Route = route;
        Databases = new(stores, workspaceLaunch: token => PlanAsync("database", token));
    }

    internal CancellationToken Lifetime { get; }
    internal CancellationToken Route { get; }
    internal DatabaseOperationWorker Databases { get; }

    internal async Task<DatabaseWorkspaceOperationLaunch> PlanAsync(string capability, CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime);
        startup.Token.ThrowIfCancellationRequested();
        if (capability is not ("database" or "files" or "redis" or "http"))
        {
            throw new InvalidOperationException("The host backend capability is not supported.");
        }
        var id = Guid.NewGuid().ToString("N");
        Operation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DatabaseWorkspaceScratch.Prepare(id);
            operation = new Operation(id, () => { lock (_gate) { _operations.Remove(id); } });
            _operations.Add(id, operation);
        }
        try
        {
            startup.Token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(_launch.Executable);
            foreach (var argument in _launch.PrefixArguments) { start.ArgumentList.Add(argument); }
            start.ArgumentList.Add(ConnectionBackendCommand.Marker);
            start.ArgumentList.Add(capability);
            start.ArgumentList.Add(id);
            return new(start, operation.CleanupAsync, Lifetime, BackendExecutionLocation.HostDirect);
        }
        catch { await operation.CleanupAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        Operation[] operations;
        lock (_gate) { _disposed = true; operations = [.. _operations.Values]; }
        await WorkspaceConnectionBackendFactory.StopOwnedResourcesAsync(
            () => new ValueTask(_lifetime.CancelAsync()),
            () => { Databases.Dispose(); return ValueTask.CompletedTask; },
            async () =>
            {
                List<Exception> failures = [];
                foreach (var operation in operations)
                {
                    try { await operation.CleanupAsync().ConfigureAwait(false); }
                    catch (Exception exception) { failures.Add(exception); }
                }
                if (failures.Count != 0) { throw new AggregateException("Host backend scratch cleanup did not complete.", failures); }
            }).ConfigureAwait(false);
        // Keep the canceled token source valid for captured launches whose
        // consumer has not registered cancellation yet.
    }

    private sealed class Operation(string id, Action released)
    {
        private readonly object _gate = new();
        private Task? _cleanup;
        internal Task CleanupAsync()
        {
            lock (_gate)
            {
                if (_cleanup is null || _cleanup.IsFaulted || _cleanup.IsCanceled) { _cleanup = CleanupCoreAsync(); }
                return _cleanup;
            }
        }
        private async Task CleanupCoreAsync()
        {
            await DatabaseWorkspaceScratch.CleanupAsync(id, CancellationToken.None).ConfigureAwait(false);
            released();
        }
    }
}
