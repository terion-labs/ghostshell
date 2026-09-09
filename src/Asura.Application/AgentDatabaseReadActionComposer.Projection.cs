using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Asura.Application;

public sealed partial class AgentDatabaseReadActionComposer
{
    private const int MaximumColumns = 256;
    private const int MaximumIndexes = 256;
    private const int MaximumIndexColumns = 128;
    private const int MaximumForeignKeys = 256;
    private const int MaximumForeignKeyColumns = 64;
    private const int MaximumCellBytes = 8_192;
    private const int MaximumResultBytes = 48 * 1_024;
    private const int MaximumSerializedResultBytes = 64 * 1_024;
    private static readonly JsonSerializerOptions ProjectionJsonOptions =
        CreateProjectionJsonOptions();
    public AgentDatabaseReadResult Project(
        AgentDatabaseReadAction action,
        DatabasePanelSessionState state)
    {
        RequireRequest<AgentDatabaseReadRequest.ReadState>(action);
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(state.Backend))
        {
            throw new ArgumentException("The database backend is invalid.", nameof(state));
        }

        var remainingBytes = MaximumResultBytes;
        ConsumeStructure(512, ref remainingBytes, nameof(state));
        ConsumeStateText(state, ref remainingBytes, nameof(state));
        var redis = state.Redis is null ? null : CopyRedisFacts(state.Redis);
        if ((state.Backend == DatabasePanelBackend.Redis) != (redis is not null))
        {
            throw new ArgumentException(
                "Database state does not match its backend.",
                nameof(state));
        }

        return EnsureSerializedBound(new AgentDatabaseReadResult.State(
            new DatabasePanelSessionState(
            state.Backend,
            CopyMetadata(state.DriverId, 256, nameof(state)),
            CopyMetadata(state.DisplayName, 256, nameof(state)),
            state.IsReady,
            CopyOptionalMetadata(state.ServerVersion, 256, nameof(state)),
            CopyOptionalMetadata(state.TlsProtocol, 128, nameof(state)),
            CopyOptionalMetadata(state.SelectedCatalog, 512, nameof(state)),
            CopyOptionalMetadata(state.SelectedSchema, 512, nameof(state)),
            redis)));
    }

    public AgentDatabaseReadResult Project(
        AgentDatabaseReadAction action,
        DatabaseObjectPage page)
    {
        var request = RequireRequest<AgentDatabaseReadRequest.ListObjects>(action);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Objects);
        if (page.Objects.Count > request.MaximumObjects)
        {
            throw new ArgumentException(
                "The database object page exceeds its authorized bound.",
                nameof(page));
        }

        var remainingBytes = MaximumResultBytes;
        ConsumeStructure(64, ref remainingBytes, nameof(page));
        foreach (var databaseObject in page.Objects)
        {
            ConsumeStructure(96, ref remainingBytes, nameof(page));
            ConsumeObjectText(databaseObject, ref remainingBytes, nameof(page));
        }

        var objects = CopyObjects(page.Objects, request.MaximumObjects, nameof(page));
        return EnsureSerializedBound(new AgentDatabaseReadResult.Objects(
            new DatabaseObjectPage(objects, page.IsTruncated)));
    }

    public AgentDatabaseReadResult Project(
        AgentDatabaseReadAction action,
        DatabaseObjectSnapshot snapshot)
    {
        var request = RequireRequest<AgentDatabaseReadRequest.DescribeObject>(action);
        ArgumentNullException.ThrowIfNull(snapshot);
        var databaseObject = CopyObject(snapshot.Object, nameof(snapshot));
        if (databaseObject.Reference != request.Reference)
        {
            throw new ArgumentException(
                "The described database object does not match the authorized reference.",
                nameof(snapshot));
        }

        var remainingBytes = MaximumResultBytes;
        ConsumeStructure(224, ref remainingBytes, nameof(snapshot));
        ConsumeObjectText(snapshot.Object, ref remainingBytes, nameof(snapshot));
        ConsumeColumnsText(snapshot.Columns, ref remainingBytes, nameof(snapshot));
        ConsumeIndexesText(snapshot.Indexes, ref remainingBytes, nameof(snapshot));
        ConsumeOptionalText(
            snapshot.ReadOnlyReason,
            ref remainingBytes,
            nameof(snapshot));
        var columns = CopyColumns(snapshot.Columns, nameof(snapshot));
        var indexes = CopyIndexes(snapshot.Indexes, nameof(snapshot));
        return EnsureSerializedBound(new AgentDatabaseReadResult.ObjectDescription(
            new DatabaseObjectSnapshot(
                databaseObject,
                columns,
                indexes,
                snapshot.CanEdit,
                CopyOptionalMetadata(snapshot.ReadOnlyReason, 2_048, nameof(snapshot)),
                snapshot.IsTruncated)));
    }

    public AgentDatabaseReadResult Project(
        AgentDatabaseReadAction action,
        DatabaseTableSnapshot snapshot)
    {
        var request = RequireRequest<AgentDatabaseReadRequest.ReadTable>(action);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Page);
        ArgumentNullException.ThrowIfNull(snapshot.Page.Result);
        var databaseObject = CopyObject(snapshot.Object, nameof(snapshot));
        if (databaseObject.Reference != request.Reference
            || snapshot.Page.Offset != request.Offset
            || snapshot.Page.Limit != request.Limit)
        {
            throw new ArgumentException(
                "The database table page does not match the authorized request.",
                nameof(snapshot));
        }

        var result = snapshot.Page.Result;
        if (result.Rows.Count > request.Limit
            || result.Columns.Count > MaximumColumns
            || result.Elapsed < TimeSpan.Zero
            || result.RowsAffected < 0
            || snapshot.Page.TotalRows < 0
            || snapshot.Page.TableRows is < 0)
        {
            throw new ArgumentException(
                "The database table page exceeds its authorized bounds.",
                nameof(snapshot));
        }

        if (request.Columns.Count > 0
            && !result.Columns.Select(column => column.Name).SequenceEqual(
                request.Columns,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The database table projection does not match its requested columns.",
                nameof(snapshot));
        }

        if (request.ExcludeColumns.Count > 0
            && result.Columns.Any(column => request.ExcludeColumns.Contains(
                column.Name,
                StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "The database table projection contains an excluded column.",
                nameof(snapshot));
        }

        var remainingBytes = MaximumResultBytes;
        ConsumeStructure(256, ref remainingBytes, nameof(snapshot));
        ConsumeObjectText(snapshot.Object, ref remainingBytes, nameof(snapshot));
        foreach (var column in result.Columns)
        {
            ArgumentNullException.ThrowIfNull(column, nameof(snapshot));
            ConsumeStructure(128, ref remainingBytes, nameof(snapshot));
            ConsumeMetadata(column.Name, ref remainingBytes, nameof(snapshot));
            ConsumeMetadata(column.DataTypeName, ref remainingBytes, nameof(snapshot));
        }

        var columns = result.Columns
            .Select(column => CopyColumnDescriptor(column, nameof(snapshot)))
            .ToArray();
        var clipped = false;
        var rows = new IReadOnlyList<string?>[result.Rows.Count];
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            ConsumeStructure(16, ref remainingBytes, nameof(snapshot));
            var sourceRow = result.Rows[rowIndex]
                ?? throw new ArgumentException(
                    "A database table row cannot be null.",
                    nameof(snapshot));
            if (sourceRow.Count != columns.Length)
            {
                throw new ArgumentException(
                    "A database table row does not match its column count.",
                    nameof(snapshot));
            }

            var row = new string?[sourceRow.Count];
            for (var cellIndex = 0; cellIndex < row.Length; cellIndex++)
            {
                ConsumeStructure(8, ref remainingBytes, nameof(snapshot));
                row[cellIndex] = CopyCell(
                    sourceRow[cellIndex],
                    request.MaximumCellBytes,
                    ref remainingBytes,
                    ref clipped);
            }

            rows[rowIndex] = Array.AsReadOnly(row);
        }

        var projectedPage = new DatabaseQueryPage(
            Array.AsReadOnly(columns),
            Array.AsReadOnly(rows),
            result.Truncated || clipped,
            result.RowsAffected,
            result.Elapsed,
            TypedRows: null);
        return EnsureSerializedBound(new AgentDatabaseReadResult.Table(
            new DatabaseTableSnapshot(
            databaseObject,
            new DatabaseTablePage(
                projectedPage,
                snapshot.Page.Offset,
                snapshot.Page.Limit,
                snapshot.Page.HasMore || clipped,
                snapshot.Page.TotalRows,
                snapshot.Page.TableRows))));
    }

    public AgentDatabaseReadResult Project(
        AgentDatabaseReadAction action,
        DatabaseSchemaGraphSnapshot graph)
    {
        var request = RequireRequest<AgentDatabaseReadRequest.SchemaGraph>(action);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(graph.Tables);
        if (graph.Tables.Count > request.MaximumObjects)
        {
            throw new ArgumentException(
                "The database schema graph exceeds its authorized object bound.",
                nameof(graph));
        }

        var remainingBytes = MaximumResultBytes;
        ConsumeStructure(64, ref remainingBytes, nameof(graph));
        foreach (var table in graph.Tables)
        {
            ArgumentNullException.ThrowIfNull(table, nameof(graph));
            ConsumeStructure(128, ref remainingBytes, nameof(graph));
            ConsumeDescriptorText(table.Object, ref remainingBytes, nameof(graph));
            ConsumeColumnsText(table.Columns, ref remainingBytes, nameof(graph));
            ConsumeForeignKeysText(table.ForeignKeys, ref remainingBytes, nameof(graph));
        }

        var tables = graph.Tables.Select(table =>
        {
            ArgumentNullException.ThrowIfNull(table);
            var descriptor = CopyDescriptor(table.Object, nameof(graph));
            var columns = CopyColumns(table.Columns, nameof(graph));
            var foreignKeys = CopyForeignKeys(table.ForeignKeys, nameof(graph));
            return new DatabaseSchemaTable(descriptor, columns, foreignKeys);
        }).ToArray();
        return EnsureSerializedBound(new AgentDatabaseReadResult.Schema(
            new DatabaseSchemaGraphSnapshot(
                Array.AsReadOnly(tables),
                graph.IsTruncated)));
    }

    public AgentDatabaseReadResult Project(
        AgentDatabaseReadAction action,
        RedisKeyPage page)
    {
        var request = RequireRequest<AgentDatabaseReadRequest.RedisScan>(action);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Keys);
        if (page.Keys.Count > request.Count)
        {
            throw new ArgumentException(
                "The Redis scan exceeds its authorized key bound.",
                nameof(page));
        }

        var remainingBytes = MaximumResultBytes;
        ConsumeStructure(64, ref remainingBytes, nameof(page));
        foreach (var key in page.Keys)
        {
            ConsumeStructure(96, ref remainingBytes, nameof(page));
            ConsumeRedisKeyText(key, ref remainingBytes, nameof(page));
        }

        ConsumeOptionalText(page.NextCursor, ref remainingBytes, nameof(page));
        var keys = page.Keys.Select(key => CopyRedisKey(key, nameof(page))).ToArray();
        return EnsureSerializedBound(new AgentDatabaseReadResult.RedisKeys(new RedisKeyPage(
            Array.AsReadOnly(keys),
            CopyOptionalMetadata(page.NextCursor, 256, nameof(page)),
            page.IsComplete)));
    }

    public AgentDatabaseReadResult Project(
        AgentDatabaseReadAction action,
        RedisKeyValueSnapshot snapshot)
    {
        var request = RequireRequest<AgentDatabaseReadRequest.RedisRead>(action);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Entries);
        if (snapshot.Key.Reference != request.Reference
            || snapshot.Entries.Count > request.MaximumEntries
            || snapshot.Length < 0)
        {
            throw new ArgumentException(
                "The Redis key result does not match its authorized request.",
                nameof(snapshot));
        }

        var remainingBytes = MaximumResultBytes;
        ConsumeStructure(192, ref remainingBytes, nameof(snapshot));
        ConsumeRedisKeyText(snapshot.Key, ref remainingBytes, nameof(snapshot));
        ConsumeOptionalText(snapshot.Limitation, ref remainingBytes, nameof(snapshot));
        var clipped = false;
        var entries = CopyRedisEntries(
            snapshot.Entries,
            ref remainingBytes,
            ref clipped,
            nameof(snapshot));
        return EnsureSerializedBound(new AgentDatabaseReadResult.RedisValue(
            new RedisKeyValueSnapshot(
                CopyRedisKey(snapshot.Key, nameof(snapshot)),
                snapshot.Length,
                entries,
                snapshot.IsTruncated || clipped,
                CopyOptionalMetadata(snapshot.Limitation, 2_048, nameof(snapshot)))));
    }

    public AgentDatabaseReadResult Project(
        AgentDatabaseReadAction action,
        RedisSearchResult result)
    {
        var request = RequireRequest<AgentDatabaseReadRequest.RedisSearch>(action);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Values);
        if (result.Total < 0 || result.Values.Count > request.Limit)
        {
            throw new ArgumentException(
                "The Redis search result exceeds its authorized bound.",
                nameof(result));
        }

        var remainingBytes = MaximumResultBytes;
        ConsumeStructure(64, ref remainingBytes, nameof(result));
        var clipped = false;
        var entries = CopyRedisEntries(
            result.Values,
            ref remainingBytes,
            ref clipped,
            nameof(result));
        return EnsureSerializedBound(new AgentDatabaseReadResult.RedisSearch(
            new RedisSearchResult(
                result.Total,
                entries,
                result.Truncated || clipped)));
    }

    public AgentDatabaseReadResult Project(
        AgentDatabaseReadAction action,
        RedisSearchIndexPage page)
    {
        var request = RequireRequest<AgentDatabaseReadRequest.RedisListIndexes>(action);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Indexes);
        if (page.Indexes.Count > request.MaximumIndexes)
        {
            throw new ArgumentException(
                "The Redis index page exceeds its authorized bound.",
                nameof(page));
        }

        var remainingBytes = MaximumResultBytes;
        ConsumeStructure(64, ref remainingBytes, nameof(page));
        var indexes = new RedisSearchIndex[page.Indexes.Count];
        for (var index = 0; index < page.Indexes.Count; index++)
        {
            var source = page.Indexes[index]
                ?? throw new ArgumentException(
                    "A Redis search index cannot be null.",
                    nameof(page));
            if (source.DocumentCount is < 0)
            {
                throw new ArgumentException(
                    "Redis search index metadata is invalid.",
                    nameof(page));
            }

            ConsumeStructure(192, ref remainingBytes, nameof(page));
            ConsumeMetadata(source.Name, ref remainingBytes, nameof(page));
            ConsumeOptionalText(source.Definition, ref remainingBytes, nameof(page));
            ConsumeOptionalText(source.Attributes, ref remainingBytes, nameof(page));
            indexes[index] = new RedisSearchIndex(
                CopyMetadata(source.Name, 256, nameof(page)),
                CopyOptionalMetadata(source.Definition, 2_048, nameof(page)),
                CopyOptionalMetadata(source.Attributes, 4_096, nameof(page)),
                source.DocumentCount);
        }

        return EnsureSerializedBound(new AgentDatabaseReadResult.RedisIndexes(
            new RedisSearchIndexPage(
                Array.AsReadOnly(indexes),
                page.IsTruncated)));
    }

    private static TRequest RequireRequest<TRequest>(
        AgentDatabaseReadAction action)
        where TRequest : AgentDatabaseReadRequest
    {
        ArgumentNullException.ThrowIfNull(action);
        ValidatePreparedAction(action);
        return action.Request as TRequest
            ?? throw new ArgumentException(
                "The database result does not match the prepared operation.",
                nameof(action));
    }

    private static IReadOnlyList<DatabaseObjectSummary> CopyObjects(
        IReadOnlyList<DatabaseObjectSummary> source,
        int maximum,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        if (source.Count > maximum)
        {
            throw new ArgumentException(
                "The database object page exceeds its authorized bound.",
                parameterName);
        }

        return Array.AsReadOnly(source
            .Select(item => CopyObject(item, parameterName))
            .ToArray());
    }

    private static DatabaseObjectSummary CopyObject(
        DatabaseObjectSummary value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!Enum.IsDefined(value.Kind))
        {
            throw new ArgumentException("A database object kind is invalid.", parameterName);
        }

        return new DatabaseObjectSummary(
            value.Reference,
            CopyMetadata(value.Name, 512, parameterName),
            value.Kind,
            CopyOptionalMetadata(value.Catalog, 512, parameterName),
            CopyOptionalMetadata(value.Schema, 512, parameterName));
    }

    private static DatabaseTableDescriptor CopyDescriptor(
        DatabaseTableDescriptor value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!Enum.IsDefined(value.Kind))
        {
            throw new ArgumentException("A database object kind is invalid.", parameterName);
        }

        return new DatabaseTableDescriptor(
            CopyMetadata(value.Name, 512, parameterName),
            value.Kind,
            CopyOptionalMetadata(value.Catalog, 512, parameterName),
            CopyOptionalMetadata(value.Schema, 512, parameterName));
    }

    private static IReadOnlyList<DatabaseColumnSchema> CopyColumns(
        IReadOnlyList<DatabaseColumnSchema> source,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        if (source.Count > MaximumColumns)
        {
            throw new ArgumentException(
                "Database column metadata exceeds its fixed bound.",
                parameterName);
        }

        return Array.AsReadOnly(source.Select(column =>
        {
            ArgumentNullException.ThrowIfNull(column, parameterName);
            if (column.Ordinal < 0
                || !Enum.IsDefined(column.ValueKind)
                || column.PrimaryKeyOrdinal < 0
                || column.Length < 0
                || column.Precision < 0
                || column.Scale < 0)
            {
                throw new ArgumentException(
                    "Database column metadata is invalid.",
                    parameterName);
            }

            return new DatabaseColumnSchema(
                CopyMetadata(column.Name, 512, parameterName),
                column.Ordinal,
                CopyMetadata(column.DataTypeName, 512, parameterName),
                column.ValueKind,
                CopyOptionalMetadata(column.ClrTypeName, 512, parameterName),
                column.IsNullable,
                column.IsPrimaryKey,
                column.PrimaryKeyOrdinal,
                column.IsIdentity,
                column.IsGenerated,
                column.IsReadOnly,
                CopyOptionalText(column.DefaultExpression, 4_096),
                column.Length,
                column.Precision,
                column.Scale);
        }).ToArray());
    }

    private static IReadOnlyList<DatabaseIndexSchema> CopyIndexes(
        IReadOnlyList<DatabaseIndexSchema> source,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        if (source.Count > MaximumIndexes)
        {
            throw new ArgumentException(
                "Database index metadata exceeds its fixed bound.",
                parameterName);
        }

        return Array.AsReadOnly(source.Select(index =>
        {
            ArgumentNullException.ThrowIfNull(index, parameterName);
            ArgumentNullException.ThrowIfNull(index.Columns, parameterName);
            if (index.Columns.Count > MaximumIndexColumns)
            {
                throw new ArgumentException(
                    "Database index columns exceed their fixed bound.",
                    parameterName);
            }

            var columns = index.Columns.Select(column =>
            {
                ArgumentNullException.ThrowIfNull(column, parameterName);
                if (column.Ordinal < 0)
                {
                    throw new ArgumentException(
                        "Database index metadata is invalid.",
                        parameterName);
                }

                return new DatabaseIndexColumn(
                    CopyOptionalMetadata(column.Name, 512, parameterName),
                    column.Ordinal,
                    column.IsDescending,
                    column.IsIncluded,
                    CopyOptionalText(column.Expression, 4_096));
            }).ToArray();
            return new DatabaseIndexSchema(
                CopyMetadata(index.Name, 512, parameterName),
                CopyMetadata(index.Kind, 256, parameterName),
                index.IsUnique,
                index.IsPrimary,
                index.IsValid,
                Array.AsReadOnly(columns),
                CopyOptionalText(index.Predicate, 4_096),
                Details: null);
        }).ToArray());
    }

    private static IReadOnlyList<DatabaseForeignKeySchema> CopyForeignKeys(
        IReadOnlyList<DatabaseForeignKeySchema> source,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        if (source.Count > MaximumForeignKeys)
        {
            throw new ArgumentException(
                "Database foreign keys exceed their fixed bound.",
                parameterName);
        }

        return Array.AsReadOnly(source.Select(key =>
        {
            ArgumentNullException.ThrowIfNull(key, parameterName);
            ArgumentNullException.ThrowIfNull(key.Columns, parameterName);
            ArgumentNullException.ThrowIfNull(key.ReferencedObject, parameterName);
            if (key.Columns.Count > MaximumForeignKeyColumns)
            {
                throw new ArgumentException(
                    "Database foreign-key columns exceed their fixed bound.",
                    parameterName);
            }

            var columns = key.Columns.Select(column =>
            {
                ArgumentNullException.ThrowIfNull(column, parameterName);
                if (column.Ordinal < 0)
                {
                    throw new ArgumentException(
                        "Database foreign-key metadata is invalid.",
                        parameterName);
                }

                return new DatabaseForeignKeyColumn(
                    CopyMetadata(column.ColumnName, 512, parameterName),
                    CopyMetadata(column.ReferencedColumnName, 512, parameterName),
                    column.Ordinal);
            }).ToArray();
            return new DatabaseForeignKeySchema(
                CopyMetadata(key.Name, 512, parameterName),
                new DatabaseObjectId(
                    CopyOptionalMetadata(key.ReferencedObject.Catalog, 512, parameterName),
                    CopyOptionalMetadata(key.ReferencedObject.Schema, 512, parameterName),
                    CopyMetadata(key.ReferencedObject.Name, 512, parameterName)),
                Array.AsReadOnly(columns));
        }).ToArray());
    }

    private static DatabaseColumnDescriptor CopyColumnDescriptor(
        DatabaseColumnDescriptor column,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(column, parameterName);
        if (!Enum.IsDefined(column.ValueKind))
        {
            throw new ArgumentException(
                "A database result column kind is invalid.",
                parameterName);
        }

        return new DatabaseColumnDescriptor(
            CopyMetadata(column.Name, 512, parameterName),
            CopyMetadata(column.DataTypeName, 512, parameterName),
            column.ValueKind,
            ClrTypeName: null,
            column.IsNullable,
            column.IsKey,
            column.IsIdentity,
            column.IsReadOnly,
            BaseColumnName: null,
            DefaultExpression: null,
            BaseObject: null,
            column.IsHidden);
    }

    private static RedisServerFacts CopyRedisFacts(RedisServerFacts facts)
    {
        if (!Enum.IsDefined(facts.Topology)
            || !Enum.IsDefined(facts.LogicalDatabases)
            || facts.SelectedDatabase < 0
            || facts.ConfiguredDatabaseCount < 0)
        {
            throw new ArgumentException("Redis server facts are invalid.", nameof(facts));
        }

        return new RedisServerFacts(
            CopyOptionalMetadata(facts.Version, 256, nameof(facts)),
            CopyOptionalMetadata(facts.Protocol, 128, nameof(facts)),
            facts.Topology,
            facts.LogicalDatabases,
            facts.SelectedDatabase,
            facts.ConfiguredDatabaseCount,
            facts.SearchAvailable,
            facts.JsonAvailable,
            facts.TimeSeriesAvailable,
            facts.ShardedPubSubAvailable,
            CopyOptionalMetadata(facts.Limitation, 2_048, nameof(facts)));
    }

    private static RedisKeyItem CopyRedisKey(
        RedisKeyItem key,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        if (key.TimeToLive < TimeSpan.Zero || key.MemoryBytes < 0)
        {
            throw new ArgumentException("Redis key metadata is invalid.", parameterName);
        }

        return new RedisKeyItem(
            key.Reference,
            CopyText(key.DisplayName, 4_096),
            CopyMetadata(key.Type, 128, parameterName),
            key.TimeToLive,
            key.MemoryBytes);
    }

    private static IReadOnlyList<RedisValueEntry> CopyRedisEntries(
        IReadOnlyList<RedisValueEntry> source,
        ref int remainingBytes,
        ref bool clipped,
        string parameterName)
    {
        var values = new RedisValueEntry[source.Count];
        for (var index = 0; index < values.Length; index++)
        {
            var value = source[index]
                ?? throw new ArgumentException(
                    "A Redis result cannot contain a null entry.",
                    parameterName);
            if (value.Score is { } score && !double.IsFinite(score))
            {
                throw new ArgumentException("A Redis score is invalid.", parameterName);
            }

            ConsumeStructure(96, ref remainingBytes, parameterName);
            values[index] = new RedisValueEntry(
                CopyBudgetedText(value.Identity, ref remainingBytes, ref clipped),
                value.Field is null
                    ? null
                    : CopyBudgetedText(value.Field, ref remainingBytes, ref clipped),
                CopyBudgetedText(value.Value, ref remainingBytes, ref clipped),
                value.Score);
        }

        return Array.AsReadOnly(values);
    }

    private static string? CopyCell(
        string? value,
        int maximumCellBytes,
        ref int remainingBytes,
        ref bool clipped) =>
        value is null
            ? null
            : CopyBudgetedText(
                value,
                ref remainingBytes,
                ref clipped,
                maximumCellBytes);

    private static string CopyBudgetedText(
        string value,
        ref int remainingBytes,
        ref bool clipped,
        int maximumValueBytes = MaximumCellBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        var byteCount = GetStrictUtf8ByteCount(value, nameof(value));
        var maximumBytes = Math.Min(maximumValueBytes, Math.Max(remainingBytes, 0));
        if (byteCount <= maximumBytes)
        {
            remainingBytes -= byteCount;
            return string.Concat(value);
        }

        clipped = true;
        var builder = new StringBuilder(Math.Min(value.Length, maximumBytes));
        var copiedBytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (copiedBytes + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            copiedBytes += rune.Utf8SequenceLength;
        }

        remainingBytes -= copiedBytes;
        return builder.ToString();
    }

    private static string CopyMetadata(
        string value,
        int maximumLength,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (GetStrictUtf8ByteCount(value, parameterName) > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Database metadata is not bounded printable text.",
                parameterName);
        }

        return string.Concat(value);
    }

    private static string? CopyOptionalMetadata(
        string? value,
        int maximumLength,
        string parameterName) =>
        value is null ? null : CopyMetadata(value, maximumLength, parameterName);

    private static string? CopyOptionalText(string? value, int maximumLength) =>
        value is null ? null : CopyText(value, maximumLength);

    private static string CopyText(string value, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (GetStrictUtf8ByteCount(value, nameof(value)) > maximumLength
            || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Database text exceeds its fixed bound.", nameof(value));
        }

        return string.Concat(value);
    }

    private static T EnsureSerializedBound<T>(T result)
        where T : AgentDatabaseReadResult
    {
        var typeInfo = (JsonTypeInfo<T>)ProjectionJsonOptions.GetTypeInfo(typeof(T));
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            result,
            typeInfo);
        if (serialized.Length > MaximumSerializedResultBytes)
        {
            throw new ArgumentException(
                "The database result exceeds its fixed serialized bound.",
                nameof(result));
        }

        return result;
    }

    private static JsonSerializerOptions CreateProjectionJsonOptions()
    {
        var resolver = JsonTypeInfoResolver.WithAddedModifier(
            AgentProjectionJsonContext.Default,
            static typeInfo =>
            {
                for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
                {
                    var property = typeInfo.Properties[index];
                    if (property.Name is
                        nameof(DatabaseQueryPage.ValueRows)
                        or nameof(DatabaseObjectSummary.DisplayName)
                        or nameof(DatabaseTableDescriptor.DisplayName)
                        or nameof(DatabaseTableDescriptor.Id)
                        or nameof(DatabaseObjectDetails.PrimaryKey)
                        or nameof(DatabaseColumnSchema.CanEdit))
                    {
                        typeInfo.Properties.RemoveAt(index);
                    }
                }
            });
        return new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = resolver,
        };
    }

    private static int GetStrictUtf8ByteCount(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Database text is not valid Unicode.",
                parameterName,
                exception);
        }
    }

    private static void ConsumeStructure(
        int bytes,
        ref int remainingBytes,
        string parameterName)
    {
        if (bytes < 0 || remainingBytes < bytes)
        {
            throw new ArgumentException(
                "The database result exceeds its fixed projection budget.",
                parameterName);
        }

        remainingBytes -= bytes;
    }

    private static void ConsumeMetadata(
        string value,
        ref int remainingBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ConsumeStructure(
            GetStrictUtf8ByteCount(value, parameterName),
            ref remainingBytes,
            parameterName);
    }

    private static void ConsumeOptionalText(
        string? value,
        ref int remainingBytes,
        string parameterName)
    {
        if (value is not null)
        {
            ConsumeMetadata(value, ref remainingBytes, parameterName);
        }
    }

    private static void ConsumeStateText(
        DatabasePanelSessionState state,
        ref int remainingBytes,
        string parameterName)
    {
        ConsumeMetadata(state.DriverId, ref remainingBytes, parameterName);
        ConsumeMetadata(state.DisplayName, ref remainingBytes, parameterName);
        ConsumeOptionalText(state.ServerVersion, ref remainingBytes, parameterName);
        ConsumeOptionalText(state.TlsProtocol, ref remainingBytes, parameterName);
        ConsumeOptionalText(state.SelectedCatalog, ref remainingBytes, parameterName);
        ConsumeOptionalText(state.SelectedSchema, ref remainingBytes, parameterName);
        if (state.Redis is not { } redis)
        {
            return;
        }

        ConsumeOptionalText(redis.Version, ref remainingBytes, parameterName);
        ConsumeOptionalText(redis.Protocol, ref remainingBytes, parameterName);
        ConsumeOptionalText(redis.Limitation, ref remainingBytes, parameterName);
    }

    private static void ConsumeObjectText(
        DatabaseObjectSummary value,
        ref int remainingBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ConsumeMetadata(value.Reference.Value, ref remainingBytes, parameterName);
        ConsumeMetadata(value.Name, ref remainingBytes, parameterName);
        ConsumeOptionalText(value.Catalog, ref remainingBytes, parameterName);
        ConsumeOptionalText(value.Schema, ref remainingBytes, parameterName);
    }

    private static void ConsumeDescriptorText(
        DatabaseTableDescriptor value,
        ref int remainingBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ConsumeMetadata(value.Name, ref remainingBytes, parameterName);
        ConsumeOptionalText(value.Catalog, ref remainingBytes, parameterName);
        ConsumeOptionalText(value.Schema, ref remainingBytes, parameterName);
    }

    private static void ConsumeColumnsText(
        IReadOnlyList<DatabaseColumnSchema> columns,
        ref int remainingBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(columns, parameterName);
        if (columns.Count > MaximumColumns)
        {
            throw new ArgumentException(
                "Database column metadata exceeds its fixed bound.",
                parameterName);
        }

        foreach (var column in columns)
        {
            ArgumentNullException.ThrowIfNull(column, parameterName);
            ConsumeStructure(192, ref remainingBytes, parameterName);
            ConsumeMetadata(column.Name, ref remainingBytes, parameterName);
            ConsumeMetadata(column.DataTypeName, ref remainingBytes, parameterName);
            ConsumeOptionalText(column.ClrTypeName, ref remainingBytes, parameterName);
            ConsumeOptionalText(column.DefaultExpression, ref remainingBytes, parameterName);
        }
    }

    private static void ConsumeIndexesText(
        IReadOnlyList<DatabaseIndexSchema> indexes,
        ref int remainingBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(indexes, parameterName);
        if (indexes.Count > MaximumIndexes)
        {
            throw new ArgumentException(
                "Database index metadata exceeds its fixed bound.",
                parameterName);
        }

        foreach (var index in indexes)
        {
            ArgumentNullException.ThrowIfNull(index, parameterName);
            ArgumentNullException.ThrowIfNull(index.Columns, parameterName);
            if (index.Columns.Count > MaximumIndexColumns)
            {
                throw new ArgumentException(
                    "Database index columns exceed their fixed bound.",
                    parameterName);
            }

            ConsumeStructure(160, ref remainingBytes, parameterName);
            ConsumeMetadata(index.Name, ref remainingBytes, parameterName);
            ConsumeMetadata(index.Kind, ref remainingBytes, parameterName);
            ConsumeOptionalText(index.Predicate, ref remainingBytes, parameterName);
            foreach (var column in index.Columns)
            {
                ArgumentNullException.ThrowIfNull(column, parameterName);
                ConsumeStructure(96, ref remainingBytes, parameterName);
                ConsumeOptionalText(column.Name, ref remainingBytes, parameterName);
                ConsumeOptionalText(column.Expression, ref remainingBytes, parameterName);
            }
        }
    }

    private static void ConsumeForeignKeysText(
        IReadOnlyList<DatabaseForeignKeySchema> foreignKeys,
        ref int remainingBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(foreignKeys, parameterName);
        if (foreignKeys.Count > MaximumForeignKeys)
        {
            throw new ArgumentException(
                "Database foreign keys exceed their fixed bound.",
                parameterName);
        }

        foreach (var key in foreignKeys)
        {
            ArgumentNullException.ThrowIfNull(key, parameterName);
            ArgumentNullException.ThrowIfNull(key.ReferencedObject, parameterName);
            ArgumentNullException.ThrowIfNull(key.Columns, parameterName);
            if (key.Columns.Count > MaximumForeignKeyColumns)
            {
                throw new ArgumentException(
                    "Database foreign-key columns exceed their fixed bound.",
                    parameterName);
            }

            ConsumeStructure(160, ref remainingBytes, parameterName);
            ConsumeMetadata(key.Name, ref remainingBytes, parameterName);
            ConsumeMetadata(key.ReferencedObject.Name, ref remainingBytes, parameterName);
            ConsumeOptionalText(key.ReferencedObject.Catalog, ref remainingBytes, parameterName);
            ConsumeOptionalText(key.ReferencedObject.Schema, ref remainingBytes, parameterName);
            foreach (var column in key.Columns)
            {
                ArgumentNullException.ThrowIfNull(column, parameterName);
                ConsumeStructure(96, ref remainingBytes, parameterName);
                ConsumeMetadata(column.ColumnName, ref remainingBytes, parameterName);
                ConsumeMetadata(column.ReferencedColumnName, ref remainingBytes, parameterName);
            }
        }
    }

    private static void ConsumeRedisKeyText(
        RedisKeyItem key,
        ref int remainingBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        ConsumeMetadata(key.Reference.Value, ref remainingBytes, parameterName);
        ConsumeMetadata(key.DisplayName, ref remainingBytes, parameterName);
        ConsumeMetadata(key.Type, ref remainingBytes, parameterName);
    }
}
