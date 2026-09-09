using System.Data.Common;
using Asura.Application;

namespace Asura.Databases;

/// <summary>
/// Converts optional ADO.NET result metadata into Asura-owned descriptors.
/// Providers may omit any <see cref="DbColumn"/> field, so reader field metadata
/// remains the portable fallback.
/// </summary>
internal static class DatabaseColumnMaterializer
{
    public static IReadOnlyList<DatabaseColumnDescriptor> DescribeColumns(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.FieldCount == 0)
        {
            return [];
        }

        IReadOnlyList<DbColumn> schema;
        try
        {
            schema = reader.GetColumnSchema();
        }
        catch (NotSupportedException)
        {
            schema = [];
        }
        catch (InvalidOperationException)
        {
            // Microsoft.Data.Sqlite throws here for a command that produced no
            // result set even though FieldCount was already queried safely.
            schema = [];
        }

        var columns = new DatabaseColumnDescriptor[reader.FieldCount];
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
        {
            var column = FindColumn(schema, ordinal);
            columns[ordinal] = column is null
                ? DescribeReaderColumn(reader, ordinal)
                : DescribeColumn(column, ordinal, reader);
        }

        return columns;
    }

    public static DatabaseColumnDescriptor DescribeColumn(
        DbColumn column,
        int fallbackOrdinal,
        DbDataReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentOutOfRangeException.ThrowIfNegative(fallbackOrdinal);

        var ordinal = column.ColumnOrdinal ?? fallbackOrdinal;
        var clrType = column.DataType ?? TryGetFieldType(reader, ordinal);
        var name = FirstNonBlank(column.ColumnName, TryGetName(reader, ordinal))
            ?? $"Column{fallbackOrdinal + 1}";
        var dataTypeName = FirstNonBlank(
                column.DataTypeName,
                TryGetDataTypeName(reader, ordinal),
                clrType?.Name)
            ?? "object";
        // KeyInfo may append provider-owned hidden key columns that were not
        // part of the user's projection. They can still be displayed by the
        // generic reader, but must never be treated as proof that the visible
        // query is an exact editable table projection.
        var hasUnsafeProjectionShape = column.IsHidden == true || column.IsAliased == true;
        var baseTableName = hasUnsafeProjectionShape
            ? null
            : FirstNonBlank(column.BaseTableName);
        var baseObject = baseTableName is null
            ? null
            : new DatabaseObjectId(
                FirstNonBlank(column.BaseCatalogName),
                FirstNonBlank(column.BaseSchemaName),
                baseTableName);

        return new DatabaseColumnDescriptor(
            name,
            dataTypeName,
            DatabaseValueClassifier.Classify(clrType, dataTypeName),
            ClrTypeName: clrType?.FullName,
            IsNullable: column.AllowDBNull,
            IsKey: column.IsKey == true,
            IsIdentity: column.IsIdentity == true || column.IsAutoIncrement == true,
            IsReadOnly: column.IsReadOnly == true || column.IsExpression == true,
            BaseColumnName: hasUnsafeProjectionShape
                ? null
                : FirstNonBlank(column.BaseColumnName),
            BaseObject: baseObject,
            IsHidden: column.IsHidden == true);
    }

    private static DatabaseColumnDescriptor DescribeReaderColumn(
        DbDataReader reader,
        int ordinal)
    {
        var clrType = reader.GetFieldType(ordinal);
        var dataTypeName = FirstNonBlank(reader.GetDataTypeName(ordinal), clrType.Name)
            ?? "object";
        return new DatabaseColumnDescriptor(
            FirstNonBlank(reader.GetName(ordinal)) ?? $"Column{ordinal + 1}",
            dataTypeName,
            DatabaseValueClassifier.Classify(clrType, dataTypeName),
            clrType.FullName);
    }

    private static DbColumn? FindColumn(IReadOnlyList<DbColumn> schema, int ordinal)
    {
        foreach (var column in schema)
        {
            if (column.ColumnOrdinal == ordinal)
            {
                return column;
            }
        }

        return ordinal < schema.Count ? schema[ordinal] : null;
    }

    private static Type? TryGetFieldType(DbDataReader? reader, int ordinal) =>
        reader is not null && ordinal < reader.FieldCount
            ? reader.GetFieldType(ordinal)
            : null;

    private static string? TryGetName(DbDataReader? reader, int ordinal) =>
        reader is not null && ordinal < reader.FieldCount
            ? reader.GetName(ordinal)
            : null;

    private static string? TryGetDataTypeName(DbDataReader? reader, int ordinal) =>
        reader is not null && ordinal < reader.FieldCount
            ? reader.GetDataTypeName(ordinal)
            : null;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
