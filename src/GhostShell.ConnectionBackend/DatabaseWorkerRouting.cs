using System.Text.Json.Serialization;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.ConnectionBackend;

internal sealed record DatabaseEndpointRequest(long Id, string Host, int Port);
internal sealed record DatabaseEndpointReply(long Id, int? LocalPort);

[JsonSerializable(typeof(DatabaseEndpointRequest))]
[JsonSerializable(typeof(DatabaseEndpointReply))]
internal sealed partial class DatabaseRoutingJsonContext : JsonSerializerContext;

/// <summary>
/// Typed endpoint exchanges on the existing private worker pipes. The caller
/// chooses the permitted phase; neither request nor reply carries route authority.
/// </summary>
internal sealed class DatabaseWorkerRouting(DatabaseWorkerRoute? route) : IAsyncDisposable
{
    internal const int MaximumRetainedEndpoints = 32;
    private readonly Dictionary<(string Host, int Port), IDatabaseTunnelLease> _leases = [];
    private readonly Dictionary<IDatabaseTunnelLease, Task> _closing = [];
    private readonly object _leaseGate = new();
    private long _lastRequest;
    private volatile bool _disposed;
    private Task? _disposal;

    internal async Task<byte[]> ReadControlAsync(Stream input, Stream output, bool allowEndpointOpen, CancellationToken token)
    {
        while (true)
        {
            var control = await DatabaseOperationProtocol.ReadFrameAsync(input, 32, token).ConfigureAwait(false);
            if (!control.AsSpan().SequenceEqual("endpoint-open"u8)) { return control; }
            if (!allowEndpointOpen || route is null || _disposed)
            {
                throw new InvalidDataException("The database worker requested a route outside its permitted phase.");
            }
            var request = await DatabaseOperationProtocol.ReadMetadataAsync(input,
                DatabaseRoutingJsonContext.Default.DatabaseEndpointRequest, token).ConfigureAwait(false);
            ValidateEndpoint(request.Host, request.Port);
            if (request.Id <= 0 || request.Id != _lastRequest + 1)
            {
                throw new InvalidDataException("The database route request identity is invalid.");
            }
            _lastRequest = request.Id;
            route.Lifetime.ThrowIfCancellationRequested();
            int? port = null;
            var endpoint = (request.Host, request.Port);
            lock (_leaseGate)
            {
                if (_leases.TryGetValue(endpoint, out var existing) && !existing.IsClosed) { port = existing.LocalPort; }
            }
            if (port is not null)
            {
                await DatabaseOperationProtocol.WriteMetadataAsync(output, new DatabaseEndpointReply(request.Id, port),
                    DatabaseRoutingJsonContext.Default.DatabaseEndpointReply, token).ConfigureAwait(false);
                continue;
            }
            await ReleaseClosedEntriesAsync(token).ConfigureAwait(false);
            var canOpen = false;
            lock (_leaseGate)
            {
                if (_leases.TryGetValue(endpoint, out var existing)) { port = existing.LocalPort; }
                else { canOpen = !_disposed && _leases.Count < MaximumRetainedEndpoints; }
            }
            if (port is null && !canOpen) { throw new DatabaseRouteEndpointBudgetException(); }
            if (port is not null)
            {
                await DatabaseOperationProtocol.WriteMetadataAsync(output, new DatabaseEndpointReply(request.Id, port),
                    DatabaseRoutingJsonContext.Default.DatabaseEndpointReply, token).ConfigureAwait(false);
                continue;
            }
            var opening = OpenWithDeadlineAsync(route, request, token);
            IDatabaseTunnelLease? opened = null;
            try
            {
                var lease = await opening.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                opened = lease;
                if (lease.LocalPort is < 1 or > 65535)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    throw new InvalidDataException("The parent route returned an invalid endpoint.");
                }
                bool accepted;
                lock (_leaseGate)
                {
                    accepted = !_disposed;
                    if (accepted) { _leases.Add(endpoint, lease); }
                }
                if (!accepted)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    throw new OperationCanceledException("The database worker route ended.", token);
                }
                port = lease.LocalPort;
            }
            catch (Exception exception)
            {
                // OpenAsync owns late cancellation cleanup. Observe its eventual
                // failure even when an underlying transport ignores its deadline.
                if (opened is null)
                {
                    _ = ReleaseLateOpenAsync(opening);
                }
                token.ThrowIfCancellationRequested();
                route.Lifetime.ThrowIfCancellationRequested();
                if (DatabaseRouteEndpointBudgetException.IsCauseOf(exception))
                {
                    throw new DatabaseRouteEndpointBudgetException();
                }
                if (exception is TimeoutException)
                {
                    // End this worker exchange instead of allowing a hostile
                    // child to accumulate further cancellation-ignoring opens.
                    throw new IOException("The captured database endpoint did not open within its deadline.");
                }
            }
            await DatabaseOperationProtocol.WriteMetadataAsync(output, new DatabaseEndpointReply(request.Id, port),
                DatabaseRoutingJsonContext.Default.DatabaseEndpointReply, token).ConfigureAwait(false);
        }
    }

    private async Task ReleaseClosedEntriesAsync(CancellationToken token)
    {
        KeyValuePair<(string Host, int Port), IDatabaseTunnelLease>[] entries;
        lock (_leaseGate) { entries = [.. _leases]; }
        var closing = entries.Where(entry => entry.Value.IsClosed).Select(entry => BeginClose(entry.Value)).ToArray();
        try { await Task.WhenAll(closing).WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { throw new IOException("A closed database forward could not be released within its cleanup deadline."); }
    }

    private Task BeginClose(IDatabaseTunnelLease lease)
    {
        lock (_leaseGate)
        {
            if (_closing.TryGetValue(lease, out var existing)) { return existing; }
            var closing = Task.Run(async () =>
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                lock (_leaseGate)
                {
                    foreach (var endpoint in _leases.Where(entry => ReferenceEquals(entry.Value, lease)).Select(entry => entry.Key).ToArray())
                    {
                        _leases.Remove(endpoint);
                    }
                    _closing.Remove(lease);
                }
            });
            _closing.Add(lease, closing);
            _ = ObserveCloseAsync(closing);
            return closing;
        }
    }

    private static async Task ObserveCloseAsync(Task closing)
    {
        try { await closing.ConfigureAwait(false); }
        catch { SecretSafeDiagnosticProjection.WriteStandardError("database.route.cleanup.failed", SecretSafeDiagnosticKind.Unexpected); }
    }

    private static async Task<IDatabaseTunnelLease> OpenWithDeadlineAsync(DatabaseWorkerRoute route,
        DatabaseEndpointRequest request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, route.Lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        return await route.OpenAsync(request.Host, request.Port, deadline.Token).ConfigureAwait(false);
    }

    private static async Task ReleaseLateOpenAsync(Task<IDatabaseTunnelLease> opening)
    {
        IDatabaseTunnelLease lease;
        try { lease = await opening.ConfigureAwait(false); }
        catch { return; }
        try { await Task.Run(async () => await lease.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false); }
        catch { SecretSafeDiagnosticProjection.WriteStandardError("database.route.late-cleanup.failed", SecretSafeDiagnosticKind.Unexpected); }
    }

    internal static void ValidateEndpoint(string? host, int port)
    {
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(host) || host.Length > 1024
            || host.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)
                || character is '/' or '\\' or '@' or '?' or '#')
            || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw new InvalidDataException("The database route endpoint is invalid.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_leaseGate)
        {
            if (_disposal is null)
            {
                _disposed = true;
                _disposal = DisposeCoreAsync([.. _leases.Values]);
            }
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync(IDatabaseTunnelLease[] leases)
    {
        try { await Task.WhenAll(leases.Select(BeginClose)).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch { throw new IOException("A database route could not be released completely within its cleanup deadline. Late cleanup remains observed."); }
    }
}

/// <summary>
/// Child-side tunnel adapter. One owner reads replies. Cancellation after a
/// request poisons this pipe exchange, so a late reply cannot authorize a retry.
/// </summary>
internal sealed class ParentEndpointTunnelFactory(Stream input, Stream output) : IDatabaseTunnelFactory, IAsyncDisposable
{
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextRequest;
    private bool _failed;
    private int _disposed;

    public CancellationToken RouteLifetime => _lifetime.Token;

    public async ValueTask<IDatabaseTunnelLease> OpenAsync(ConnectionProfile connection, string targetHost, int targetPort,
        CancellationToken cancellationToken)
    {
        DatabaseWorkerRouting.ValidateEndpoint(targetHost, targetPort);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _exchange.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            if (_failed) { throw new IOException("The parent database route is no longer available."); }
            var request = new DatabaseEndpointRequest(checked(++_nextRequest), targetHost, targetPort);
            try
            {
                await DatabaseOperationProtocol.WriteFrameAsync(output, "endpoint-open"u8.ToArray(), cancellation.Token).ConfigureAwait(false);
                await DatabaseOperationProtocol.WriteMetadataAsync(output, request,
                    DatabaseRoutingJsonContext.Default.DatabaseEndpointRequest, cancellation.Token).ConfigureAwait(false);
                var reply = await ReadReplyAsync(cancellation.Token).ConfigureAwait(false);
                if (reply.Id != request.Id || reply.LocalPort is null or < 1 or > 65535)
                {
                    throw new IOException("The parent database route denied the endpoint.");
                }
                return new EndpointLease(reply.LocalPort.Value, _lifetime.Token);
            }
            catch
            {
                _failed = true;
                await CancelLifetimeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _exchange.Release(); }
    }

    private async Task<DatabaseEndpointReply> ReadReplyAsync(CancellationToken token)
    {
        var reading = DatabaseOperationProtocol.ReadMetadataAsync(input,
            DatabaseRoutingJsonContext.Default.DatabaseEndpointReply, token);
        try { return await reading.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            // Standard-input streams may not interrupt a native pending read.
            // The exchange is poisoned, so no later operation reads this pipe;
            // observe the old read until process teardown releases it.
            _ = reading.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private async Task CancelLifetimeAsync()
    {
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch { SecretSafeDiagnosticProjection.WriteStandardError("database.route.child-cancellation.failed", SecretSafeDiagnosticKind.Unexpected); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        await CancelLifetimeAsync().ConfigureAwait(false);
        // Cancellation releases a pending read and any queued provider retry.
        // Wait for its finally block before disposing the reply-owner semaphore.
        await _exchange.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _exchange.Release();
        _exchange.Dispose();
        _lifetime.Dispose();
    }

    // Only the parent owns the actual forwarding listener. Child connection
    // disposal must not pretend to dispose or replace that parent's authority.
    private sealed class EndpointLease(int localPort, CancellationToken lifetime) : IDatabaseTunnelLease
    {
        public int LocalPort => localPort;
        public bool IsClosed => lifetime.IsCancellationRequested;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
