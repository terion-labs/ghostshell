using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Asura.Application;

namespace Asura.Databases;

internal sealed partial class RelationalDatabasePanelSession
{
    private const int MaximumColumns = 256;
    private const int MaximumIndexes = 256;
    private const int MaximumIndexColumns = 128;
    private const int MaximumForeignKeys = 128;
    private const int MaximumForeignKeyColumns = 64;
    private const int MaximumMetadataBytes = 1_024;
    private const int MaximumExpressionBytes = 4_096;
    private const int MaximumCellBytes = 16 * 1_024;
    private const int MaximumProjectedPayloadBytes = 48 * 1_024;
    private const int MaximumSerializedResultBytes = 64 * 1_024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private DatabaseObjectPage ProjectObjectPage(
        IReadOnlyList<DatabaseTableDescriptor> source,
        bool providerTruncated)
    {
        var remainingBytes = MaximumProjectedPayloadBytes;
        var objects = new List<DatabaseObjectSummary>(source.Count);
        foreach (var descriptor in source)
        {
            var safeDescriptor = CopyDescriptor(descriptor, nameof(source));
            var cost = DescriptorCost(safeDescriptor) + 192;
            if (cost > remainingBytes)
            {
                providerTruncated = true;
                break;
            }

            remainingBytes -= cost;
            objects.Add(ProjectObject(safeDescriptor));
        }

        var result = new DatabaseObjectPage(
            Array.AsReadOnly(objects.ToArray()),
            providerTruncated || objects.Count < source.Count);
        EnsureSerializedBound(result, nameof(source));
        return result;
    }

    private DatabaseObjectSnapshot ProjectObjectSnapshot(
        DatabaseObjectDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(details.Columns);
        ArgumentNullException.ThrowIfNull(details.Indexes);
        var databaseObject = ProjectObject(details.Object);
        var readOnlyReason = CopyOptionalText(
            details.ReadOnlyReason,
            MaximumExpressionBytes,
            nameof(details));
        var remainingBytes = MaximumProjectedPayloadBytes
            - ObjectSummaryCost(databaseObject)
            - (readOnlyReason is null ? 0 : Utf8Length(readOnlyReason))
            - 512;
        if (remainingBytes < 0)
        {
            throw new InvalidDataException(
                "The database object metadata exceeds the projected payload bound.");
        }

        var columns = new List<DatabaseColumnSchema>();
        foreach (var source in details.Columns.Take(MaximumColumns))
        {
            var column = CopyColumn(source, nameof(details));
            var cost = ColumnCost(column);
            if (cost > remainingBytes)
            {
                break;
            }

            remainingBytes -= cost;
            columns.Add(column);
        }

        var indexes = new List<DatabaseIndexSchema>();
        foreach (var source in details.Indexes.Take(MaximumIndexes))
        {
            var index = CopyIndex(source, nameof(details));
            var cost = IndexCost(index);
            if (cost > remainingBytes)
            {
                break;
            }

            remainingBytes -= cost;
            indexes.Add(index);
        }

        var truncated = details.Columns.Count > columns.Count
            || details.Indexes.Count > indexes.Count
            || details.Indexes.Take(indexes.Count)
                .Zip(indexes)
                .Any(pair => pair.First.Columns.Count > pair.Second.Columns.Count);
        var result = new DatabaseObjectSnapshot(
            databaseObject,
            Array.AsReadOnly(columns.ToArray()),
            Array.AsReadOnly(indexes.ToArray()),
            details.CanEdit,
            readOnlyReason,
            truncated);
        EnsureSerializedBound(result, nameof(details));
        return result;
    }

    private DatabaseTablePage ProjectTablePage(
        DatabaseTablePage page,
        DatabaseTableQuery query)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Result);
        ArgumentNullException.ThrowIfNull(page.Result.Columns);
        ArgumentNullException.ThrowIfNull(page.Result.Rows);
        if (page.Offset != query.Offset
            || page.Limit != query.Limit
            || page.Result.Rows.Count > query.Limit
            || page.Result.Columns.Count > MaximumColumns
            || page.Result.RowsAffected < 0
            || page.Result.Elapsed < TimeSpan.Zero
            || page.TotalRows < 0)
        {
            throw new InvalidDataException(
                "The database provider returned a table page outside its fixed bounds.");
        }

        var columns = page.Result.Columns
            .Select(column => CopyResultColumn(column, nameof(page)))
            .ToArray();
        var remainingBytes = MaximumProjectedPayloadBytes - 4_096;
        foreach (var column in columns)
        {
            remainingBytes -= ResultColumnCost(column);
        }

        if (remainingBytes < 0)
        {
            throw new InvalidDataException(
                "The database result columns exceed the projected payload bound.");
        }

        var truncated = page.Result.Truncated;
        var rows = new IReadOnlyList<string?>[page.Result.Rows.Count];
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var source = page.Result.Rows[rowIndex]
                ?? throw new InvalidDataException(
                    "The database provider returned a null table row.");
            if (source.Count != columns.Length)
            {
                throw new InvalidDataException(
                    "The database provider returned a row with the wrong cell count.");
            }

            var row = new string?[source.Count];
            remainingBytes -= 16 + (source.Count * 8);
            if (remainingBytes < 0)
            {
                truncated = true;
                rows = [.. rows.Take(rowIndex)];
                break;
            }

            for (var cellIndex = 0; cellIndex < row.Length; cellIndex++)
            {
                row[cellIndex] = source[cellIndex] is { } text
                    ? CopyCell(text, ref remainingBytes, ref truncated)
                    : null;
            }

            rows[rowIndex] = Array.AsReadOnly(row);
        }

        // Provider-specific RawValue objects never cross the hosted boundary.
        var safeResult = new DatabaseQueryPage(
            Array.AsReadOnly(columns),
            Array.AsReadOnly(rows),
            truncated,
            page.Result.RowsAffected,
            page.Result.Elapsed,
            TypedRows: null);
        var result = new DatabaseTablePage(
            safeResult,
            page.Offset,
            page.Limit,
            page.HasMore || truncated,
            page.TotalRows,
            page.TableRows);
        EnsureSerializedBound(result, nameof(page));
        return result;
    }

    private DatabaseSchemaGraphSnapshot ProjectSchemaGraph(
        DatabaseSchemaGraph graph,
        int maximumObjects)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(graph.Tables);
        var truncated = graph.Tables.Count > maximumObjects;
        var remainingBytes = MaximumProjectedPayloadBytes;
        var tables = new List<DatabaseSchemaTable>();
        foreach (var table in graph.Tables.Take(maximumObjects))
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(table.Columns);
            ArgumentNullException.ThrowIfNull(table.ForeignKeys);
            var descriptor = CopyDescriptor(table.Object, nameof(graph));
            var descriptorCost = DescriptorCost(descriptor) + 192;
            if (descriptorCost > remainingBytes)
            {
                truncated = true;
                break;
            }

            remainingBytes -= descriptorCost;
            var columns = new List<DatabaseColumnSchema>();
            foreach (var source in table.Columns.Take(MaximumColumns))
            {
                var column = CopyColumn(source, nameof(graph));
                var cost = ColumnCost(column);
                if (cost > remainingBytes)
                {
                    truncated = true;
                    break;
                }

                remainingBytes -= cost;
                columns.Add(column);
            }

            var foreignKeys = new List<DatabaseForeignKeySchema>();
            foreach (var source in table.ForeignKeys.Take(MaximumForeignKeys))
            {
                var key = CopyForeignKey(source, nameof(graph), ref truncated);
                var cost = ForeignKeyCost(key);
                if (cost > remainingBytes)
                {
                    truncated = true;
                    break;
                }

                remainingBytes -= cost;
                foreignKeys.Add(key);
            }

            truncated |= table.Columns.Count > columns.Count
                || table.ForeignKeys.Count > foreignKeys.Count;
            _ = ProjectObject(descriptor);
            tables.Add(new DatabaseSchemaTable(
                descriptor,
                Array.AsReadOnly(columns.ToArray()),
                Array.AsReadOnly(foreignKeys.ToArray())));
        }

        var result = new DatabaseSchemaGraphSnapshot(
            Array.AsReadOnly(tables.ToArray()),
            truncated);
        EnsureSerializedBound(result, nameof(graph));
        return result;
    }

    private static DatabaseTableDescriptor CopyDescriptor(
        DatabaseTableDescriptor value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!Enum.IsDefined(value.Kind))
        {
            throw new InvalidDataException("The database provider returned an invalid object kind.");
        }

        return new DatabaseTableDescriptor(
            CopyMetadata(value.Name, parameterName),
            value.Kind,
            CopyOptionalMetadata(value.Catalog, parameterName),
            CopyOptionalMetadata(value.Schema, parameterName));
    }

    private static DatabaseColumnSchema CopyColumn(
        DatabaseColumnSchema column,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(column, parameterName);
        if (column.Ordinal < 0
            || !Enum.IsDefined(column.ValueKind)
            || column.PrimaryKeyOrdinal < 0
            || column.Length < 0
            || column.Precision < 0
            || column.Scale < 0)
        {
            throw new InvalidDataException(
                "The database provider returned invalid column metadata.");
        }

        return new DatabaseColumnSchema(
            CopyMetadata(column.Name, parameterName),
            column.Ordinal,
            CopyMetadata(column.DataTypeName, parameterName),
            column.ValueKind,
            CopyOptionalMetadata(column.ClrTypeName, parameterName),
            column.IsNullable,
            column.IsPrimaryKey,
            column.PrimaryKeyOrdinal,
            column.IsIdentity,
            column.IsGenerated,
            column.IsReadOnly,
            CopyOptionalText(column.DefaultExpression, MaximumExpressionBytes, parameterName),
            column.Length,
            column.Precision,
            column.Scale);
    }

    private static DatabaseIndexSchema CopyIndex(
        DatabaseIndexSchema index,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(index, parameterName);
        ArgumentNullException.ThrowIfNull(index.Columns, parameterName);
        var columns = index.Columns.Take(MaximumIndexColumns).Select(column =>
        {
            ArgumentNullException.ThrowIfNull(column, parameterName);
            if (column.Ordinal < 0)
            {
                throw new InvalidDataException(
                    "The database provider returned invalid index metadata.");
            }

            return new DatabaseIndexColumn(
                CopyOptionalMetadata(column.Name, parameterName),
                column.Ordinal,
                column.IsDescending,
                column.IsIncluded,
                CopyOptionalText(column.Expression, MaximumExpressionBytes, parameterName));
        }).ToArray();
        return new DatabaseIndexSchema(
            CopyMetadata(index.Name, parameterName),
            CopyMetadata(index.Kind, parameterName),
            index.IsUnique,
            index.IsPrimary,
            index.IsValid,
            Array.AsReadOnly(columns),
            CopyOptionalText(index.Predicate, MaximumExpressionBytes, parameterName),
            Details: null);
    }

    private static DatabaseForeignKeySchema CopyForeignKey(
        DatabaseForeignKeySchema key,
        string parameterName,
        ref bool truncated)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        ArgumentNullException.ThrowIfNull(key.ReferencedObject, parameterName);
        ArgumentNullException.ThrowIfNull(key.Columns, parameterName);
        var columns = key.Columns.Take(MaximumForeignKeyColumns).Select(column =>
        {
            ArgumentNullException.ThrowIfNull(column, parameterName);
            if (column.Ordinal < 0)
            {
                throw new InvalidDataException(
                    "The database provider returned invalid foreign-key metadata.");
            }

            return new DatabaseForeignKeyColumn(
                CopyMetadata(column.ColumnName, parameterName),
                CopyMetadata(column.ReferencedColumnName, parameterName),
                column.Ordinal);
        }).ToArray();
        truncated |= key.Columns.Count > columns.Length;
        return new DatabaseForeignKeySchema(
            CopyMetadata(key.Name, parameterName),
            new DatabaseObjectId(
                CopyOptionalMetadata(key.ReferencedObject.Catalog, parameterName),
                CopyOptionalMetadata(key.ReferencedObject.Schema, parameterName),
                CopyMetadata(key.ReferencedObject.Name, parameterName)),
            Array.AsReadOnly(columns));
    }

    private static DatabaseColumnDescriptor CopyResultColumn(
        DatabaseColumnDescriptor column,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(column, parameterName);
        if (!Enum.IsDefined(column.ValueKind))
        {
            throw new InvalidDataException(
                "The database provider returned an invalid result column kind.");
        }

        return new DatabaseColumnDescriptor(
            CopyMetadata(column.Name, parameterName),
            CopyMetadata(column.DataTypeName, parameterName),
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

    private static string CopyCell(
        string value,
        ref int remainingBytes,
        ref bool truncated)
    {
        ArgumentNullException.ThrowIfNull(value);
        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The database provider returned invalid Unicode text.",
                exception);
        }

        var allowed = Math.Min(MaximumCellBytes, Math.Max(remainingBytes, 0));
        if (bytes <= allowed)
        {
            remainingBytes -= bytes;
            return string.Concat(value);
        }

        truncated = true;
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > allowed)
            {
                break;
            }

            builder.Append(rune);
            used += rune.Utf8SequenceLength;
        }

        remainingBytes -= used;
        return builder.ToString();
    }

    private static string CopyMetadata(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsControl) || Utf8Length(value) > MaximumMetadataBytes)
        {
            throw new InvalidDataException(
                "The database provider returned invalid bounded metadata.");
        }

        return string.Concat(value);
    }

    private static int DescriptorCost(DatabaseTableDescriptor value) =>
        128
        + Utf8Length(value.Name)
        + OptionalUtf8Length(value.Catalog)
        + OptionalUtf8Length(value.Schema);

    private static int ObjectSummaryCost(DatabaseObjectSummary value) =>
        192
        + Utf8Length(value.Reference.Value)
        + Utf8Length(value.Name)
        + OptionalUtf8Length(value.Catalog)
        + OptionalUtf8Length(value.Schema);

    private static int ColumnCost(DatabaseColumnSchema value) =>
        256
        + Utf8Length(value.Name)
        + Utf8Length(value.DataTypeName)
        + OptionalUtf8Length(value.ClrTypeName)
        + OptionalUtf8Length(value.DefaultExpression);

    private static int IndexCost(DatabaseIndexSchema value) =>
        192
        + Utf8Length(value.Name)
        + Utf8Length(value.Kind)
        + OptionalUtf8Length(value.Predicate)
        + value.Columns.Sum(column =>
            128
            + OptionalUtf8Length(column.Name)
            + OptionalUtf8Length(column.Expression));

    private static int ForeignKeyCost(DatabaseForeignKeySchema value) =>
        192
        + Utf8Length(value.Name)
        + OptionalUtf8Length(value.ReferencedObject.Catalog)
        + OptionalUtf8Length(value.ReferencedObject.Schema)
        + Utf8Length(value.ReferencedObject.Name)
        + value.Columns.Sum(column =>
            96
            + Utf8Length(column.ColumnName)
            + Utf8Length(column.ReferencedColumnName));

    private static int ResultColumnCost(DatabaseColumnDescriptor value) =>
        192 + Utf8Length(value.Name) + Utf8Length(value.DataTypeName);

    private static string? CopyOptionalMetadata(string? value, string parameterName) =>
        value is null ? null : CopyMetadata(value, parameterName);

    private static string? CopyOptionalText(
        string? value,
        int maximumBytes,
        string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (Utf8Length(value) > maximumBytes)
        {
            throw new InvalidDataException(
                "The database provider returned oversized text metadata.");
        }

        return string.Concat(value);
    }

    private static int Utf8Length(string value)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The database provider returned invalid Unicode text.",
                exception);
        }
    }

    private static int OptionalUtf8Length(string? value) =>
        value is null ? 0 : Utf8Length(value);

    private static void EnsureSerializedBound<T>(T value, string parameterName)
    {
        try
        {
            var typeInfo = (JsonTypeInfo<T>)DatabaseProjectionJsonContext.Default
                .Options.GetTypeInfo(typeof(T));
            if (JsonSerializer.SerializeToUtf8Bytes(value, typeInfo).Length
                > MaximumSerializedResultBytes)
            {
                throw new InvalidDataException(
                    "The database provider result exceeds the serialized byte bound.");
            }
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException(
                "The database provider result cannot be serialized safely.",
                exception);
        }

        _ = parameterName;
    }
}
