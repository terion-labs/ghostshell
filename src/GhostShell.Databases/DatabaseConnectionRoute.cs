using System.Net;
using System.Net.Sockets;
using GhostShell.Application;
using GhostShell.Core;
using Microsoft.Data.SqlClient;

namespace GhostShell.Databases;

/// <summary>Owns one captured database route, its provider pool identity and forwards.</summary>
public sealed class DatabaseConnectionRoute : IAsyncDisposable
{
    internal const int MaximumRetainedEndpoints = 32;
    private readonly object _gate = new();
    private readonly IDatabaseTunnelFactory _factory;
    private readonly bool _ownsFactory;
    private readonly ConnectionProfile _profile;
    private readonly CancellationTokenSource _lifetime;
    private readonly Dictionary<(string Host, int Port), Task<RouteLease>> _cached = [];
    private readonly Dictionary<long, Task<RouteLease>> _leases = [];
    private long _sequence;
    private Task? _disposal;

    internal DatabaseConnectionRoute(IDatabaseTunnelFactory factory, bool ownsFactory, ConnectionProfile profile)
    {
        _factory = factory;
        _ownsFactory = ownsFactory;
        _profile = profile;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(factory.RouteLifetime);
        Lifetime = _lifetime.Token;
        SqlTransport = new SqlConnectionTcpTransport(OpenSocketAsync, Lifetime);
        WorkerCapability = new DatabaseWorkerRoute(OpenIndependentAsync, Lifetime);
    }

    public CancellationToken Lifetime { get; }
    internal SqlConnectionTcpTransport SqlTransport { get; }
    internal DatabaseWorkerRoute WorkerCapability { get; }

    internal async Task<int> GetLocalPortAsync(string host, int port, CancellationToken token)
    {
        ValidateEndpoint(host, port);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime);
        Task<RouteLease>? current;
        lock (_gate) { ThrowIfClosed(); _cached.TryGetValue((host, port), out current); }
        if (current?.IsCompletedSuccessfully == true)
        {
            var existing = await current.ConfigureAwait(false);
            if (!existing.IsClosed)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                return existing.LocalPort;
            }
        }
        await ReleaseClosedEntriesAsync(cancellation.Token).ConfigureAwait(false);
        Task<RouteLease> pending;
        lock (_gate)
        {
            ThrowIfClosed();
            if (!_cached.TryGetValue((host, port), out pending!))
            {
                if (_cached.Count >= MaximumRetainedEndpoints)
                {
                    throw new DatabaseRouteEndpointBudgetException("This database target retained 32 route endpoints. Restart GhostShell to release retained forwards before opening another endpoint.");
                }
                pending = StartOpen(host, port, Lifetime);
                _cached.Add((host, port), pending);
            }
        }
        try
        {
            var lease = await pending.WaitAsync(TimeSpan.FromSeconds(15), cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (lease.OpeningExpired) { throw new TimeoutException("The database endpoint opening deadline expired."); }
            if (lease.IsClosed) { throw new IOException("The database route closed before its endpoint could be used."); }
            return lease.LocalPort;
        }
        catch (Exception exception)
        {
            if (exception is TimeoutException) { ObserveCanceledOpening(pending); }
            if (pending.IsFaulted || pending.IsCanceled)
            {
                lock (_gate)
                {
                    if (_cached.GetValueOrDefault((host, port)) == pending) { _cached.Remove((host, port)); }
                }
                await DisposePendingAsync(pending).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task ReleaseClosedEntriesAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        Task<RouteLease>[] completed;
        lock (_gate) { ThrowIfClosed(); completed = [.. _cached.Values.Where(task => task.IsCompletedSuccessfully)]; }
        foreach (var pending in completed)
        {
            var lease = await pending.ConfigureAwait(false);
            if (!lease.IsClosed) { continue; }
            try { await lease.DisposeAsync().AsTask().WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !Lifetime.IsCancellationRequested)
            {
                throw new TimeoutException("A closed database forward could not be released within its cleanup deadline.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new IOException("A closed database forward could not be released.", exception);
            }
            lock (_gate)
            {
                foreach (var endpoint in _cached.Where(pair => pair.Value == pending).Select(pair => pair.Key).ToArray())
                {
                    _cached.Remove(endpoint);
                }
            }
        }
    }

    private async ValueTask<IDatabaseTunnelLease> OpenIndependentAsync(string host, int port, CancellationToken token)
    {
        ValidateEndpoint(host, port);
        Task<RouteLease> pending;
        lock (_gate) { ThrowIfClosed(); pending = StartOpen(host, port, token); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime);
        try
        {
            var lease = await pending.WaitAsync(TimeSpan.FromSeconds(15), cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (lease.OpeningExpired) { throw new TimeoutException("The database endpoint opening deadline expired."); }
            if (lease.IsClosed) { throw new IOException("The database route closed before its endpoint could be used."); }
            return lease;
        }
        catch
        {
            // A canceled transfer never publishes a late forward; the holder
            // also retains this task until its owned cleanup has completed.
            ObserveCanceledOpening(pending);
            throw;
        }
    }

    private void ObserveCanceledOpening(Task<RouteLease> pending)
    {
        // The task remains in _leases until this observer completes. Disposal
        // also awaits the same owned task; no caller cancellation source is used.
        _ = DisposePendingAsync(pending);
    }

    private Task<RouteLease> StartOpen(string host, int port, CancellationToken token)
    {
        var id = ++_sequence;
        var pending = OpenTrackedAsync(id, host, port, token);
        _leases.Add(id, pending);
        return pending;
    }

    private async Task<RouteLease> OpenTrackedAsync(long id, string host, int port, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime);
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        var lease = await _factory.OpenAsync(_profile, host, port, cancellation.Token).ConfigureAwait(false);
        // Always retain a returned resource, even when its opening expired.
        // The waiter/owner disposes it without publishing authority to a caller.
        return new RouteLease(this, id, lease, cancellation.IsCancellationRequested);
    }

    private async Task<Socket> OpenSocketAsync(string host, int port, bool parallel,
        SqlConnectionIPAddressPreference preference, CancellationToken token)
    {
        // The captured route resolves the logical destination. This local hop
        // has one IPv4 endpoint; provider intent never authorizes parent DNS.
        _ = parallel;
        _ = preference;
        var localPort = await GetLocalPortAsync(host, port, token).ConfigureAwait(false);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, localPort, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_disposal is not null, this);
        Lifetime.ThrowIfCancellationRequested();
    }

    private static void ValidateEndpoint(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("The database route endpoint is invalid.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { return new ValueTask(_disposal ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        // Cancellation closes provider-owned sockets before forwarding drain.
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task<RouteLease>[] pending;
        lock (_gate) { pending = [.. _leases.Values]; _cached.Clear(); }
        var drain = Task.WhenAll(pending.Select(DisposePendingAsync));
        var timedOut = false;
        try { await drain.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (TimeoutException) { timedOut = true; }
        try
        {
            if (_ownsFactory)
            {
                if (_factory is IAsyncDisposable asyncDisposable) { await asyncDisposable.DisposeAsync().ConfigureAwait(false); }
                else if (_factory is IDisposable disposable) { disposable.Dispose(); }
            }
        }
        finally { _lifetime.Dispose(); }
        if (timedOut)
        {
            // Every pending disposal remains observed by the drain task and
            // removes its tracked entry on completion, even after this deadline.
            throw new IOException("Database route cleanup did not finish within its deadline. Pending forwards remain canceled and their late completion is observed.");
        }
    }

    private async Task DisposePendingAsync(Task<RouteLease> pending)
    {
        RouteLease lease;
        try
        {
            lease = await pending.ConfigureAwait(false);
        }
        catch
        {
            // A failed opening returned no resource. Failed resource disposal,
            // in contrast, retains its tracked entry and cached capacity.
            lock (_gate)
            {
                foreach (var id in _leases.Where(pair => pair.Value == pending).Select(pair => pair.Key).ToArray())
                {
                    _leases.Remove(id);
                }
            }
            return;
        }
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch { /* The lease's observer reports the closed diagnostic. */ }
    }

    private sealed class RouteLease(DatabaseConnectionRoute owner, long id, IDatabaseTunnelLease lease, bool openingExpired) : IDatabaseTunnelLease
    {
        private readonly object _gate = new();
        private Task? _disposal;
        public bool OpeningExpired => openingExpired;
        public int LocalPort => lease.LocalPort;
        public bool IsClosed { get { lock (_gate) { return openingExpired || _disposal is not null || lease.IsClosed; } } }
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposal is null)
                {
                    _disposal = Task.Run(DisposeCoreAsync);
                    _ = ObserveDisposalAsync(_disposal);
                }
                return new ValueTask(_disposal);
            }
        }
        private async Task DisposeCoreAsync()
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            lock (owner._gate) { owner._leases.Remove(id); }
        }

        private static async Task ObserveDisposalAsync(Task disposal)
        {
            try { await disposal.ConfigureAwait(false); }
            catch { SecretSafeDiagnosticProjection.WriteStandardError("database.route.cleanup.failed", SecretSafeDiagnosticKind.Unexpected); }
        }
    }
}
