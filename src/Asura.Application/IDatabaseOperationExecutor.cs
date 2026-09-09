using System.Text.Json.Serialization;

namespace Asura.Application;

/// <summary>Logical server identity; transport is owned by the selected backend.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatabaseWorkerConnection(string DriverId, string ConnectionString)
{
    public override string ToString() => "[private database worker connection]";
}

/// <summary>
/// Executes complete provider operations outside the desktop process. Returned
/// results own detached content; no provider object or live reader crosses this
/// boundary. Connection secrets travel only through parent-owned private IPC.
/// </summary>
public interface IDatabaseOperationExecutor
{
    Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
        DatabaseWorkerConnection connection,
        CancellationToken cancellationToken);

    Task<DatabaseSchemaGraph> GetDatabaseSchemaGraphAsync(
        DatabaseWorkerConnection connection,
        CancellationToken cancellationToken);

    Task<SqlCatalogSnapshot> GetSqlCatalogAsync(
        DatabaseWorkerConnection connection,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListDatabasesAsync(
        DatabaseWorkerConnection connection,
        CancellationToken cancellationToken);

    Task<DatabaseSessionInfo> DescribeSessionAsync(
        DatabaseWorkerConnection connection,
        CancellationToken cancellationToken);

    Task<DatabaseObjectDetails> GetObjectDetailsAsync(
        DatabaseWorkerConnection connection,
        DatabaseTableDescriptor databaseObject,
        CancellationToken cancellationToken);

    Task<long> CountQueryRowsAsync(
        DatabaseWorkerConnection connection,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        IReadOnlyList<DatabaseFilterCondition> filters,
        CancellationToken cancellationToken);

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
