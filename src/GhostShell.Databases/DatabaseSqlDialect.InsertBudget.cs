using System.Text;
using GhostShell.Application;

namespace GhostShell.Databases;

internal sealed partial class DatabaseSqlDialect
{
    // Check clipboard-only callers before quoting/hex expansion allocates the
    // statement. Default callers retain the existing unbounded formatting API.
    private void EnsureInsertFits(DatabaseObjectId table, DatabaseObjectDetails details,
        IReadOnlyList<DatabaseColumnEdit> values, int maximumUtf8Bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUtf8Bytes, 1);
        if (maximumUtf8Bytes == int.MaxValue) { return; }
        long remaining = maximumUtf8Bytes;
        void Add(long bytes)
        {
            if (bytes > remaining)
            {
                throw new InvalidDataException("INSERT script exceeds its UTF-8 size limit. Export the current page to a file instead.");
            }
            remaining -= bytes;
        }
        Add("INSERT INTO ".Length);
        var first = true;
        foreach (var part in ObjectComponents(table))
        {
            if (!first) { Add(1); }
            Add(QuotedIdentifierSize(part));
            first = false;
        }
        if (values.Count == 0)
        {
            if (Family == DatabaseFamily.Oracle)
            {
                Add(" () VALUES (DEFAULT);".Length);
                Add(QuotedIdentifierSize(OracleDefaultColumn(details).Name));
            }
            else
            {
                Add(Family == DatabaseFamily.MySql ? " () VALUES ();".Length : " DEFAULT VALUES;".Length);
            }
            return;
        }

        Add(" () VALUES ();".Length + 4L * (values.Count - 1));
        var columns = details.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        foreach (var value in values)
        {
            Add(QuotedIdentifierSize(value.ColumnName));
            Add(value.State == DatabaseEditValueState.Null ? 4 : InsertLiteralSize(value.Value!, columns[value.ColumnName]));
        }
    }

    private long QuotedIdentifierSize(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var escaped = Family switch
        {
            DatabaseFamily.MySql or DatabaseFamily.ClickHouse => '`',
            DatabaseFamily.SqlServer => ']',
            _ => '"',
        };
        return 2L + Encoding.UTF8.GetByteCount(identifier) + identifier.AsSpan().Count(escaped);
    }

    private long InsertLiteralSize(object value, DatabaseColumnSchema column)
    {
        if (value is string text)
        {
            var payload = (long)Encoding.UTF8.GetByteCount(text);
            if (Family is DatabaseFamily.PostgreSql or DatabaseFamily.MySql or DatabaseFamily.ClickHouse)
            {
                payload *= 2;
            }
            else
            {
                payload += text.AsSpan().Count('\'');
            }
            // Empty formatting supplies the exact dialect wrapper and optional
            // PostgreSQL JSON/XML cast without duplicating that syntax here.
            return payload + Encoding.UTF8.GetByteCount(FormatTextLiteral(string.Empty, column));
        }
        var binaryLength = value switch
        {
            byte[] bytes => bytes.Length,
            ReadOnlyMemory<byte> bytes => bytes.Length,
            Memory<byte> bytes => bytes.Length,
            _ => -1,
        };
        if (binaryLength >= 0)
        {
            return 2L * binaryLength + Encoding.UTF8.GetByteCount(FormatBinaryLiteral([]));
        }

        // The operation worker transmits JSON as string/content, not DOMs.
        // Legacy trusted JsonElement callers retain GetRawText compatibility;
        // fixed scalar/date literals are already bounded by their CLR types.
        return Encoding.UTF8.GetByteCount(FormatInsertLiteral(value, column));
    }
}
