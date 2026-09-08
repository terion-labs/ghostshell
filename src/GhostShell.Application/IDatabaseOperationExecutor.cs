using System.Text.Json.Serialization;

namespace GhostShell.Application;

/// <summary>A route resource refusal, never an instruction to retry a statement.</summary>
public sealed class DatabaseRouteEndpointBudgetException(string message = "The database operation reached its 32 retained route-endpoint budget.") : IOException(message)
{
    public static bool IsCauseOf(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DatabaseRouteEndpointBudgetException) { return true; }
        }
        return false;
    }
}

/// <summary>Logical server identity and optional parent-owned transport route.</summary>
public sealed record DatabaseWorkerConnection(string DriverId, string ConnectionString, int? LocalRoutePort = null)
{
    [JsonIgnore]
    public DatabaseWorkerRoute? Route { get; init; }

    public override string ToString() => "[private database worker connection]";
}

/// <summary>
/// Parent-only authority for endpoint opens on an already-selected route. The
/// opener returns independently owned leases, never a shared cached lease.
/// Neither this delegate nor its authority is serialized to a worker.
/// </summary>
public sealed class DatabaseWorkerRoute(
    Func<string, int, CancellationToken, ValueTask<IDatabaseTunnelLease>> open,
    CancellationToken lifetime)
{
    public CancellationToken Lifetime { get; } = lifetime;

    public async ValueTask<IDatabaseTunnelLease> OpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime);
        linked.Token.ThrowIfCancellationRequested();
        var lease = await open(host, port, linked.Token).ConfigureAwait(false);
        if (linked.IsCancellationRequested)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        return lease;
    }
}

/// <summary>
/// Executes complete provider operations outside the desktop process. Returned
/// results own detached content; no provider object or live reader crosses this
/// boundary. Connection secrets travel only through parent-owned private IPC.
/// </summary>
public interface IDatabaseOperationExecutor
{
    Task<DatabaseQueryPage> QueryAsync(
        DatabaseWorkerConnection connection,
        string sql,
        int maximumRows,
        bool requestProvenance,
        CancellationToken cancellationToken);

    Task<DatabaseTablePage> ReadQueryAsync(
        DatabaseWorkerConnection connection,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        DatabaseTableQuery query,
        CancellationToken cancellationToken);

    Task<DatabaseTablePage> ReadTableAsync(
        DatabaseWorkerConnection connection,
        DatabaseTableDescriptor table,
        DatabaseTableQuery query,
        CancellationToken cancellationToken);

    Task<DatabaseMutationResult> ApplyTableChangesAsync(
        DatabaseWorkerConnection connection,
        DatabaseTableDescriptor table,
        DatabaseTableChanges changes,
        CancellationToken cancellationToken);
}

/// <summary>Dispatch may have committed; retrying could duplicate a mutation.</summary>
public sealed class DatabaseMutationOutcomeUnknownException : IOException
{
    public DatabaseMutationOutcomeUnknownException()
        : base("The database worker stopped before confirming the mutation outcome. Changes may have committed. Reload and verify the database before trying again.")
    {
    }

    public DatabaseMutationOutcomeUnknownException(string message) : base(message) { }

    public DatabaseMutationOutcomeUnknownException(string message, Exception innerException)
        : base(message, innerException) { }
}
