using System.Text.Json.Serialization.Metadata;
using GhostShell.Application;
using GhostShell.Databases;

namespace GhostShell.ConnectionBackend;

internal sealed partial class DatabaseOperationWorker
{
    public async Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken) =>
        await ReadMetadataAsync(DatabaseWorkerOperation.ListTables, connection,
            DatabaseOperationJsonContext.Default.DatabaseTableDescriptorArray, cancellationToken).ConfigureAwait(false);

    public Task<DatabaseSchemaGraph> GetDatabaseSchemaGraphAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken) =>
        ReadMetadataAsync(DatabaseWorkerOperation.SchemaGraph, connection, DatabaseOperationJsonContext.Default.DatabaseSchemaGraph, cancellationToken);

    public Task<SqlCatalogSnapshot> GetSqlCatalogAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken) =>
        ReadMetadataAsync(DatabaseWorkerOperation.SqlCatalog, connection, DatabaseOperationJsonContext.Default.SqlCatalogSnapshot, cancellationToken);

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken) =>
        await ReadMetadataAsync(DatabaseWorkerOperation.ListDatabases, connection,
            DatabaseOperationJsonContext.Default.StringArray, cancellationToken).ConfigureAwait(false);

    public Task<DatabaseSessionInfo> DescribeSessionAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken) =>
        ReadMetadataAsync(DatabaseWorkerOperation.DescribeSession, connection, DatabaseOperationJsonContext.Default.DatabaseSessionInfo, cancellationToken);

    public Task<DatabaseObjectDetails> GetObjectDetailsAsync(DatabaseWorkerConnection connection, DatabaseTableDescriptor databaseObject, CancellationToken cancellationToken) =>
        ReadMetadataAsync(DatabaseWorkerOperation.ObjectDetails, connection, DatabaseOperationJsonContext.Default.DatabaseObjectDetails, cancellationToken, databaseObject);

    public Task<long> CountQueryRowsAsync(DatabaseWorkerConnection connection, string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns, IReadOnlyList<DatabaseFilterCondition> filters, CancellationToken cancellationToken) =>
        RunAsync(new(DatabaseWorkerOperation.CountQueryRows, connection, string.Empty, SourceColumns: sourceColumns.Count), async stream =>
        {
            await DatabaseOperationValueProtocol.WriteParameterAsync(stream, sourceSql, cancellationToken).ConfigureAwait(false);
            foreach (var column in sourceColumns)
            {
                await DatabaseOperationProtocol.WriteMetadataAsync(stream, column,
                    DatabaseOperationJsonContext.Default.DatabaseColumnDescriptor, cancellationToken).ConfigureAwait(false);
            }
            await DatabaseOperationProtocol.WriteQueryAsync(stream, new DatabaseTableQuery(Filters: filters, Sorts: [], Offset: 0, Limit: 1), cancellationToken).ConfigureAwait(false);
        }, (stream, token) => ReadDocumentResultAsync(stream, DatabaseOperationJsonContext.Default.Int64, token), cancellationToken);

    private Task<T> ReadMetadataAsync<T>(DatabaseWorkerOperation operation, DatabaseWorkerConnection connection,
        JsonTypeInfo<T> type, CancellationToken token, DatabaseTableDescriptor? table = null) =>
        RunAsync(new(operation, connection, string.Empty, Table: table), _ => Task.CompletedTask,
            (stream, cancellation) => ReadDocumentResultAsync(stream, type, cancellation), token);

    private static async Task<T> ReadDocumentResultAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken token)
    {
        var result = await DatabaseMetadataProtocol.ReadAsync(stream, type, token).ConfigureAwait(false);
        await DatabaseOperationProtocol.ExpectAsync(stream, "complete"u8.ToArray(), token).ConfigureAwait(false);
        return result;
    }

    private static ChildResult MetadataResult<T>(T value, JsonTypeInfo<T> type) =>
        new(WriteMetadata: stream => DatabaseMetadataProtocol.WriteAsync(stream, value, type, CancellationToken.None));

    private static async Task<ChildResult?> ExecuteMetadataAsync(DatabasePanelClient client,
        DatabaseOperationRequest request, Parameters parameters)
    {
        var target = request.Connection;
        return request.Operation switch
        {
            DatabaseWorkerOperation.ListTables => MetadataResult(
                [.. await client.ListTablesAsync(target.DriverId, target.ConnectionString, null, CancellationToken.None).ConfigureAwait(false)],
                DatabaseOperationJsonContext.Default.DatabaseTableDescriptorArray),
            DatabaseWorkerOperation.SchemaGraph => MetadataResult(
                await client.GetDatabaseSchemaGraphAsync(target.DriverId, target.ConnectionString, null, CancellationToken.None).ConfigureAwait(false),
                DatabaseOperationJsonContext.Default.DatabaseSchemaGraph),
            DatabaseWorkerOperation.SqlCatalog => MetadataResult(
                await client.GetSqlCatalogAsync(target.DriverId, target.ConnectionString, null, CancellationToken.None).ConfigureAwait(false),
                DatabaseOperationJsonContext.Default.SqlCatalogSnapshot),
            DatabaseWorkerOperation.ListDatabases => MetadataResult(
                [.. await client.ListDatabasesAsync(target.DriverId, target.ConnectionString, null, CancellationToken.None).ConfigureAwait(false)],
                DatabaseOperationJsonContext.Default.StringArray),
            DatabaseWorkerOperation.DescribeSession => MetadataResult(
                await client.DescribeSessionAsync(target.DriverId, target.ConnectionString, null, CancellationToken.None).ConfigureAwait(false),
                DatabaseOperationJsonContext.Default.DatabaseSessionInfo),
            DatabaseWorkerOperation.ObjectDetails => MetadataResult(
                await client.GetObjectDetailsAsync(target.DriverId, target.ConnectionString, null,
                    request.Table ?? throw new InvalidDataException("An object is required."), CancellationToken.None).ConfigureAwait(false),
                DatabaseOperationJsonContext.Default.DatabaseObjectDetails),
            DatabaseWorkerOperation.CountQueryRows => MetadataResult(
                await client.CountQueryRowsAsync(target.DriverId, target.ConnectionString, null, parameters.Sql!, parameters.Columns,
                    parameters.Query!.Filters, CancellationToken.None).ConfigureAwait(false), DatabaseOperationJsonContext.Default.Int64),
            _ => null,
        };
    }
}
