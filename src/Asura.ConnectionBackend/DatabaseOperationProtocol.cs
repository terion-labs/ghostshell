using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Asura.Application;

namespace Asura.ConnectionBackend;

internal enum DatabaseWorkerOperation
{
    Query, ReadQuery, ReadTable, ApplyChanges,
    ListTables, SchemaGraph, SqlCatalog, ListDatabases, DescribeSession, ObjectDetails, CountQueryRows,
}

internal sealed class DatabaseResultRetentionException : IOException
{
    public DatabaseResultRetentionException()
        : base("The result's column and display metadata exceeds the available retention budget, capped at 256 MiB.") { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DatabaseOperationRequest(DatabaseWorkerOperation Operation, DatabaseWorkerConnection Connection,
    string ContentDirectory, int MaximumRows = 0, bool Provenance = false, DatabaseTableDescriptor? Table = null,
    int SourceColumns = 0, long? SqliteSnapshotBytes = null,
    DatabaseConnectionMaterial[]? ConnectionMaterials = null);
internal sealed record DatabaseQueryShape(int Filters, IReadOnlyList<DatabaseSort> Sorts, int Offset, int Limit,
    IReadOnlyList<string>? Columns, IReadOnlyList<string>? ExcludeColumns);
internal sealed record DatabaseFilterShape(string ColumnName, DatabaseFilterOperator Operator);
internal sealed record DatabaseEditShape(string ColumnName, DatabaseEditValueState State);
internal sealed record DatabaseChangesShape(int Inserts, int Updates, int Deletes);
internal sealed record DatabaseResultShape(int Columns, int Rows, bool Truncated, int RowsAffected, long ElapsedTicks,
    int Offset = 0, int Limit = 0, bool HasMore = false, long TotalRows = 0, long? TableRows = null);

[JsonSerializable(typeof(DatabaseOperationRequest))]
[JsonSerializable(typeof(DatabaseQueryShape))]
[JsonSerializable(typeof(DatabaseFilterShape))]
[JsonSerializable(typeof(DatabaseEditShape))]
[JsonSerializable(typeof(DatabaseChangesShape))]
[JsonSerializable(typeof(DatabaseResultShape))]
[JsonSerializable(typeof(DatabaseColumnDescriptor))]
[JsonSerializable(typeof(DatabaseMutationResult))]
[JsonSerializable(typeof(DatabaseProviderDiagnostic))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(DatabaseTableDescriptor[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(DatabaseSchemaGraph))]
[JsonSerializable(typeof(SqlCatalogSnapshot))]
[JsonSerializable(typeof(DatabaseSessionInfo))]
[JsonSerializable(typeof(DatabaseObjectDetails))]
internal sealed partial class DatabaseOperationJsonContext : JsonSerializerContext;

/// <summary>Bounded metadata frames and individually streamed values, never a whole-result JSON document.</summary>
internal static class DatabaseOperationProtocol
{
    internal const int MaximumMetadataBytes = 32 * 1024;
    internal const int MaximumResultMetadataBytes = 256 * 1024 * 1024;
    internal const int MaximumResultInlineBytes = 16 * 1024 * 1024;
    internal const int MaximumColumns = MaximumResultMetadataBytes / 256;
    internal const int MaximumParameterEntries = 1_000_000;

    internal static async Task WriteMetadataAsync<T>(Stream stream, T value, JsonTypeInfo<T> type, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (bytes.Length > MaximumMetadataBytes)
        {
            throw new InvalidDataException("The database worker metadata exceeds its supported frame size.");
        }
        await WriteFrameAsync(stream, bytes, token).ConfigureAwait(false);
    }

    internal static async Task<T> ReadMetadataAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken token)
    {
        var bytes = await ReadFrameAsync(stream, MaximumMetadataBytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, type)
            ?? throw new InvalidDataException("The database worker metadata is invalid.");
    }

    internal static void ValidateCount(int count, int maximum)
    {
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException("The database worker metadata count is invalid.");
        }
    }

    internal static async Task ExpectAsync(Stream stream, ReadOnlyMemory<byte> expected, CancellationToken token)
    {
        var actual = await ReadFrameAsync(stream, 32, token).ConfigureAwait(false);
        if (!actual.AsSpan().SequenceEqual(expected.Span))
        {
            throw new InvalidDataException("The database worker control response is invalid.");
        }
    }

    internal static async Task WriteQueryAsync(Stream stream, DatabaseTableQuery query, CancellationToken token)
    {
        await WriteMetadataAsync(stream, new DatabaseQueryShape(query.Filters.Count, query.Sorts, query.Offset,
            query.Limit, query.Columns, query.ExcludeColumns), DatabaseOperationJsonContext.Default.DatabaseQueryShape, token).ConfigureAwait(false);
        foreach (var filter in query.Filters)
        {
            await WriteMetadataAsync(stream, new DatabaseFilterShape(filter.ColumnName, filter.Operator),
                DatabaseOperationJsonContext.Default.DatabaseFilterShape, token).ConfigureAwait(false);
            await DatabaseOperationValueProtocol.WriteParameterAsync(stream, filter.Value, token).ConfigureAwait(false);
        }
    }

    internal static async Task<DatabaseTableQuery> ReadQueryAsync(Stream stream, CancellationToken token)
    {
        var shape = await ReadMetadataAsync(stream, DatabaseOperationJsonContext.Default.DatabaseQueryShape, token).ConfigureAwait(false);
        ValidateCount(shape.Filters, MaximumParameterEntries);
        if (shape.Offset < 0 || shape.Limit < 1 || shape.Sorts is null)
        {
            throw new InvalidDataException("The database query shape is invalid.");
        }
        var filters = new List<DatabaseFilterCondition>();
        for (var index = 0; index < shape.Filters; index++)
        {
            var filter = await ReadMetadataAsync(stream, DatabaseOperationJsonContext.Default.DatabaseFilterShape, token).ConfigureAwait(false);
            if (!Enum.IsDefined(filter.Operator) || string.IsNullOrEmpty(filter.ColumnName))
            {
                throw new InvalidDataException("The database query filter is invalid.");
            }
            filters.Add(new(filter.ColumnName, filter.Operator,
                await DatabaseOperationValueProtocol.ReadParameterAsync(stream, token).ConfigureAwait(false)));
        }
        return new(filters, shape.Sorts, shape.Offset, shape.Limit, shape.Columns, shape.ExcludeColumns);
    }

    internal static async Task WriteChangesAsync(Stream stream, DatabaseTableChanges changes, CancellationToken token)
    {
        await WriteMetadataAsync(stream, new DatabaseChangesShape(changes.Inserts.Count, changes.Updates.Count, changes.Deletes.Count),
            DatabaseOperationJsonContext.Default.DatabaseChangesShape, token).ConfigureAwait(false);
        foreach (var row in changes.Inserts) { await WriteEditsAsync(stream, row.Values, token).ConfigureAwait(false); }
        foreach (var row in changes.Updates)
        {
            await WriteEditsAsync(stream, row.Keys, token).ConfigureAwait(false);
            await WriteEditsAsync(stream, row.Changes, token).ConfigureAwait(false);
            await WriteEditsAsync(stream, row.OriginalValues, token).ConfigureAwait(false);
        }
        foreach (var row in changes.Deletes)
        {
            await WriteEditsAsync(stream, row.Keys, token).ConfigureAwait(false);
            await WriteEditsAsync(stream, row.OriginalValues, token).ConfigureAwait(false);
        }
    }

    internal static async Task<DatabaseTableChanges> ReadChangesAsync(Stream stream, CancellationToken token)
    {
        var shape = await ReadMetadataAsync(stream, DatabaseOperationJsonContext.Default.DatabaseChangesShape, token).ConfigureAwait(false);
        ValidateCount(shape.Inserts, MaximumParameterEntries);
        ValidateCount(shape.Updates, MaximumParameterEntries);
        ValidateCount(shape.Deletes, MaximumParameterEntries);
        var inserts = new List<DatabaseInsertedRow>();
        var updates = new List<DatabaseUpdatedRow>();
        var deletes = new List<DatabaseDeletedRow>();
        for (var index = 0; index < shape.Inserts; index++)
        {
            inserts.Add(new(await ReadEditsAsync(stream, token).ConfigureAwait(false)));
        }
        for (var index = 0; index < shape.Updates; index++)
        {
            updates.Add(new(await ReadEditsAsync(stream, token).ConfigureAwait(false),
                await ReadEditsAsync(stream, token).ConfigureAwait(false), await ReadEditsAsync(stream, token).ConfigureAwait(false)));
        }
        for (var index = 0; index < shape.Deletes; index++)
        {
            deletes.Add(new(await ReadEditsAsync(stream, token).ConfigureAwait(false),
                await ReadEditsAsync(stream, token).ConfigureAwait(false)));
        }
        return new(inserts, updates, deletes);
    }

    private static async Task WriteEditsAsync(Stream stream, IReadOnlyList<DatabaseColumnEdit> edits, CancellationToken token)
    {
        await WriteMetadataAsync(stream, edits.Count, DatabaseOperationJsonContext.Default.Int32, token).ConfigureAwait(false);
        foreach (var edit in edits)
        {
            await WriteMetadataAsync(stream, new DatabaseEditShape(edit.ColumnName, edit.State),
                DatabaseOperationJsonContext.Default.DatabaseEditShape, token).ConfigureAwait(false);
            await DatabaseOperationValueProtocol.WriteParameterAsync(stream, edit.Value, token).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<DatabaseColumnEdit>> ReadEditsAsync(Stream stream, CancellationToken token)
    {
        var count = await ReadMetadataAsync(stream, DatabaseOperationJsonContext.Default.Int32, token).ConfigureAwait(false);
        ValidateCount(count, MaximumColumns);
        var edits = new List<DatabaseColumnEdit>();
        for (var index = 0; index < count; index++)
        {
            var edit = await ReadMetadataAsync(stream, DatabaseOperationJsonContext.Default.DatabaseEditShape, token).ConfigureAwait(false);
            if (!Enum.IsDefined(edit.State) || string.IsNullOrEmpty(edit.ColumnName))
            {
                throw new InvalidDataException("The database row edit is invalid.");
            }
            edits.Add(new(edit.ColumnName, edit.State,
                await DatabaseOperationValueProtocol.ReadParameterAsync(stream, token).ConfigureAwait(false)));
        }
        return edits;
    }

    internal static async Task WriteResultAsync(Stream stream, DatabaseQueryPage result, DatabaseTablePage? page, CancellationToken token)
    {
        var rows = result.ValueRows;
        var shape = new DatabaseResultShape(result.Columns.Count, rows.Count, result.Truncated, result.RowsAffected,
            result.Elapsed.Ticks, page?.Offset ?? 0, page?.Limit ?? 0, page?.HasMore ?? false, page?.TotalRows ?? 0, page?.TableRows);
        await WriteMetadataAsync(stream, shape, DatabaseOperationJsonContext.Default.DatabaseResultShape, token).ConfigureAwait(false);
        foreach (var column in result.Columns)
        {
            await WriteMetadataAsync(stream, column, DatabaseOperationJsonContext.Default.DatabaseColumnDescriptor, token).ConfigureAwait(false);
        }
        foreach (var row in rows)
        {
            if (row.Count != result.Columns.Count) { throw new InvalidDataException("The database result row width is invalid."); }
            foreach (var value in row)
            {
                await DatabaseOperationValueProtocol.WriteAsync(stream, value, token).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<DatabaseTablePage> ReadResultAsync(Stream stream, int maximumRows,
        Func<DatabaseValueContentStore> createStore, CancellationToken token, long? metadataBudget = null)
    {
        var retentionLimit = metadataBudget is { } requested
            ? Math.Clamp(requested, 0, MaximumResultMetadataBytes)
            : CurrentMetadataBudget();
        var shape = await ReadMetadataAsync(stream, DatabaseOperationJsonContext.Default.DatabaseResultShape, token).ConfigureAwait(false);
        ValidateCount(shape.Columns, MaximumColumns);
        ValidateCount(shape.Rows, maximumRows);
        if (shape.ElapsedTicks < 0 || shape.Offset < 0 || shape.Limit < 0 || shape.TotalRows < 0 || shape.TableRows < 0)
        {
            throw new InvalidDataException("The database result metadata is invalid.");
        }
        var columns = new List<DatabaseColumnDescriptor>();
        // Bound retained row/cell objects before constructing the lists. This
        // is an explicit resource failure, not an additional successful page cap.
        long metadataBytes = (long)shape.Rows * (64 + (long)shape.Columns * 256);
        CheckMetadataBudget(metadataBytes, retentionLimit);
        for (var index = 0; index < shape.Columns; index++)
        {
            var bytes = await ReadFrameAsync(stream, MaximumMetadataBytes, token).ConfigureAwait(false);
            metadataBytes += 256 + bytes.Length * 2L;
            CheckMetadataBudget(metadataBytes, retentionLimit);
            var column = JsonSerializer.Deserialize(bytes, DatabaseOperationJsonContext.Default.DatabaseColumnDescriptor)
                ?? throw new InvalidDataException("The database result column is invalid.");
            if (column.Name is null || !Enum.IsDefined(column.ValueKind))
            {
                throw new InvalidDataException("The database result column is invalid.");
            }
            columns.Add(column);
        }
        DatabaseValueContentStore? store = null;
        var inlineRemaining = MaximumResultInlineBytes;
        bool ReserveInline(int bytes)
        {
            if (bytes < 0 || bytes > inlineRemaining) { return false; }
            inlineRemaining -= bytes;
            return true;
        }
        try
        {
            var rows = new List<IReadOnlyList<DatabaseValue>>();
            var displayRows = new List<IReadOnlyList<string?>>();
            for (var rowIndex = 0; rowIndex < shape.Rows; rowIndex++)
            {
                var values = new List<DatabaseValue>();
                var display = new List<string?>();
                for (var columnIndex = 0; columnIndex < shape.Columns; columnIndex++)
                {
                    var value = await DatabaseOperationValueProtocol.ReadResultAsync(stream,
                        () => store ??= createStore(), token, ReserveInline).ConfigureAwait(false);
                    metadataBytes += value.DisplayText.Length * 2L;
                    CheckMetadataBudget(metadataBytes, retentionLimit);
                    values.Add(value);
                    display.Add(value.IsNull ? null : value.DisplayText);
                }
                rows.Add(values);
                displayRows.Add(display);
            }
            await ExpectAsync(stream, "complete"u8.ToArray(), token).ConfigureAwait(false);
            var result = new DatabaseQueryPage(columns, displayRows, shape.Truncated, shape.RowsAffected,
                TimeSpan.FromTicks(shape.ElapsedTicks), rows, store);
            return new(result, shape.Offset, shape.Limit, shape.HasMore, shape.TotalRows, shape.TableRows);
        }
        catch
        {
            store?.Dispose();
            throw;
        }
    }

    private static long CurrentMetadataBudget()
    {
        var memory = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        return MetadataBudget(memory.TotalAvailableMemoryBytes, memory.MemoryLoadBytes, process.WorkingSet64);
    }

    // System load already includes this process. Taking the larger observation
    // avoids subtracting its working set twice. These sampled metrics are not a
    // reservation; the hard ceiling still applies when availability is unknown.
    internal static long MetadataBudget(long available, long systemLoad, long workingSet) => available <= 0
        ? MaximumResultMetadataBytes
        : Math.Min(MaximumResultMetadataBytes, Math.Max(0, available - Math.Max(0, Math.Max(systemLoad, workingSet))) / 4);

    private static void CheckMetadataBudget(long bytes, long limit)
    {
        if (bytes > limit)
        {
            throw new DatabaseResultRetentionException();
        }
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, int maximum, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maximum)
        {
            throw new InvalidDataException("The database worker frame is invalid.");
        }

        var bytes = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            return bytes;
        }
        catch
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }
}
