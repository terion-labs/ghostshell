using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Asura.Application;

namespace Asura.App.ViewModels;

internal enum DatabaseGridExportFormat
{
    Csv,
    Json,
    Sql,
}

/// <summary>
/// Serializes detached database values without first flattening binary and
/// collection values into page-sized strings. Clipboard callers use the same
/// writers behind a strict UTF-8 byte budget; file exports write directly to
/// their destination.
/// </summary>
internal static partial class DatabaseGridExport
{
    public const int MaximumClipboardUtf8Bytes = 16 * 1024 * 1024;

    private const int BinaryChunkBytes = 4 * 1024;
    private const int Base64ChunkBytes = 4095;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static TextWriter CreateFileWriter(Stream destination, CancellationToken token) =>
        new CancellableFileWriter(destination, token);

    private sealed class CancellableFileWriter(Stream destination, CancellationToken token)
        : StreamWriter(destination, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
    {
        public override void Write(char value) { token.ThrowIfCancellationRequested(); base.Write(value); }
        public override void Write(string? value) { token.ThrowIfCancellationRequested(); base.Write(value); }
        public override void Write(ReadOnlySpan<char> buffer) { token.ThrowIfCancellationRequested(); base.Write(buffer); }
        public override void Write(char[] buffer, int index, int count) { token.ThrowIfCancellationRequested(); base.Write(buffer, index, count); }
        public override void Flush() { token.ThrowIfCancellationRequested(); base.Flush(); }
    }

    public static string BuildClipboardText(Action<TextWriter> write, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var writer = new BoundedUtf8StringWriter(MaximumClipboardUtf8Bytes, token);
        write(writer);
        return writer.ToString();
    }

    public static void WriteCellText(
        TextWriter writer,
        DatabaseResultCellViewModel cell)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(cell);

        switch (cell.State)
        {
            case DatabaseEditValueState.Null:
                writer.Write("NULL");
                return;
            case DatabaseEditValueState.Default:
                writer.Write("DEFAULT");
                return;
        }

        if (cell.Column.ValueKind == DatabaseValueKind.Other && cell.RawValue is not DatabaseValueContent)
        {
            writer.Write(cell.Text);
            return;
        }

        if (cell.RawValue is { } value)
        {
            WriteFullValue(writer, value);
            return;
        }

        writer.Write(cell.EditText);
    }

    public static void WriteColumnValues(
        TextWriter writer,
        IReadOnlyList<DatabaseResultRowViewModel> rows,
        int ordinal)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rows);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (rowIndex > 0)
            {
                writer.Write(Environment.NewLine);
            }

            WriteCellText(writer, rows[rowIndex].Cells[ordinal]);
        }
    }

    public static void WriteRowTsv(
        TextWriter writer,
        DatabaseResultRowViewModel row) =>
        WriteDelimitedRow(writer, row.Cells, '\t', nullAsEmpty: false);

    public static void WriteCurrentPageTsv(
        TextWriter writer,
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        IReadOnlyList<DatabaseResultRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        if (columns.Count == 0)
        {
            return;
        }

        WriteDelimitedStrings(writer, columns.Select(column => column.Name), '\t');
        foreach (var row in rows)
        {
            writer.Write(Environment.NewLine);
            WriteRowTsv(writer, row);
        }
    }

    public static void WriteCsv(
        TextWriter writer,
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        IReadOnlyList<DatabaseResultRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        if (columns.Count == 0)
        {
            return;
        }

        WriteDelimitedStrings(writer, columns.Select(column => column.Name), ',');
        foreach (var row in rows)
        {
            ValidateRowWidth(columns, row);
            writer.Write(Environment.NewLine);
            WriteDelimitedRow(writer, row.Cells, ',', nullAsEmpty: true);
        }
    }

    public static void WriteJsonRow(
        Utf8JsonWriter writer,
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        DatabaseResultRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(row);
        ValidateRowWidth(columns, row);

        writer.WriteStartObject();
        foreach (var ordinal in JsonPropertyOrdinals(columns))
        {
            writer.WritePropertyName(columns[ordinal].Name);
            WriteJsonCell(writer, columns[ordinal], row.Cells[ordinal]);
        }

        writer.WriteEndObject();
    }

    public static void WriteJsonRow(
        TextWriter writer,
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        DatabaseResultRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(row);
        ValidateRowWidth(columns, row);
        WriteJsonRow(writer, columns, row, depth: 0);
    }

    public static void WriteCurrentPageJson(
        Utf8JsonWriter writer,
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        IReadOnlyList<DatabaseResultRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        writer.WriteStartArray();
        foreach (var row in rows)
        {
            WriteJsonRow(writer, columns, row);
        }

        writer.WriteEndArray();
    }

    public static void WriteCurrentPageJson(
        TextWriter writer,
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        IReadOnlyList<DatabaseResultRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        writer.Write('[');
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            writer.Write(rowIndex == 0 ? Environment.NewLine : $",{Environment.NewLine}");
            WriteJsonIndent(writer, depth: 1);
            ValidateRowWidth(columns, rows[rowIndex]);
            WriteJsonRow(writer, columns, rows[rowIndex], depth: 1);
        }

        if (rows.Count > 0)
        {
            writer.Write(Environment.NewLine);
        }

        writer.Write(']');
    }

    public static void WriteSqlInsert(
        TextWriter writer,
        DatabaseObjectId table,
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        DatabaseResultRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(row);
        ValidateRowWidth(columns, row);

        writer.Write("INSERT INTO ");
        var wroteComponent = false;
        foreach (var component in new[] { table.Catalog, table.Schema, table.Name })
        {
            if (string.IsNullOrWhiteSpace(component))
            {
                continue;
            }

            if (wroteComponent)
            {
                writer.Write('.');
            }

            WriteQuotedIdentifier(writer, component);
            wroteComponent = true;
        }

        writer.Write(" (");
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (ordinal > 0)
            {
                writer.Write(", ");
            }

            WriteQuotedIdentifier(writer, columns[ordinal].Name);
        }

        writer.Write(") VALUES (");
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (ordinal > 0)
            {
                writer.Write(", ");
            }

            WriteSqlLiteral(writer, columns[ordinal], row.Cells[ordinal]);
        }

        writer.Write(");");
    }

    public static void WriteCurrentPageSql(
        TextWriter writer,
        DatabaseObjectId table,
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        IReadOnlyList<DatabaseResultRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (rowIndex > 0)
            {
                writer.Write(Environment.NewLine);
            }

            WriteSqlInsert(writer, table, columns, rows[rowIndex]);
        }
    }

    private static void WriteDelimitedStrings(
        TextWriter writer,
        IEnumerable<string> values,
        char delimiter)
    {
        var ordinal = 0;
        foreach (var value in values)
        {
            if (ordinal++ > 0)
            {
                writer.Write(delimiter);
            }

            WriteDelimitedString(writer, value, delimiter);
        }
    }

    private static void WriteDelimitedRow(
        TextWriter writer,
        IReadOnlyList<DatabaseResultCellViewModel> cells,
        char delimiter,
        bool nullAsEmpty)
    {
        for (var ordinal = 0; ordinal < cells.Count; ordinal++)
        {
            if (ordinal > 0)
            {
                writer.Write(delimiter);
            }

            var cell = cells[ordinal];
            if (nullAsEmpty && cell.IsNull)
            {
                continue;
            }

            if (!CellTextNeedsQuote(cell, delimiter))
            {
                WriteCellText(writer, cell);
                continue;
            }

            writer.Write('"');
            var escapingWriter = new CharacterEscapingTextWriter(writer, '"');
            WriteCellText(escapingWriter, cell);
            writer.Write('"');
        }
    }

    private static void WriteDelimitedString(
        TextWriter writer,
        string value,
        char delimiter)
    {
        if (!ContainsDelimitedSpecial(value, delimiter))
        {
            writer.Write(value);
            return;
        }

        writer.Write('"');
        WriteEscaped(writer, value, '"');
        writer.Write('"');
    }

    private static bool CellTextNeedsQuote(
        DatabaseResultCellViewModel cell,
        char delimiter)
    {
        if (cell.IsNull)
        {
            return delimiter == '\t' && ContainsDelimitedSpecial("NULL", delimiter);
        }

        if (cell.IsDefault)
        {
            return ContainsDelimitedSpecial("DEFAULT", delimiter);
        }

        if (cell.Column.ValueKind == DatabaseValueKind.Other && cell.RawValue is not DatabaseValueContent)
        {
            return ContainsDelimitedSpecial(cell.Text, delimiter);
        }

        return cell.RawValue is { } value
            ? FullValueContainsDelimitedSpecial(value, delimiter)
            : ContainsDelimitedSpecial(cell.EditText, delimiter);
    }

    private static bool FullValueContainsDelimitedSpecial(object value, char delimiter)
    {
        if (value is DatabaseValueContent content)
        {
            // Legal CSV quoting preserves the decoded value and avoids a
            // complete pre-scan of a potentially huge detached field.
            return content.Kind != DatabaseValueKind.Binary;
        }

        if (value is byte[])
        {
            return false;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind is JsonValueKind.String
                or JsonValueKind.Object
                or JsonValueKind.Array;
        }

        if (value is Array array)
        {
            if (delimiter == ',' && array.Length > 1)
            {
                return true;
            }

            foreach (var item in array)
            {
                if (item is not null && FullValueContainsDelimitedSpecial(item, delimiter))
                {
                    return true;
                }
            }

            return false;
        }

        return ContainsDelimitedSpecial(FormatScalar(value), delimiter);
    }

    private static bool ContainsDelimitedSpecial(string value, char delimiter) =>
        value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0;

    private static void WriteFullValue(TextWriter writer, object value)
    {
        switch (value)
        {
            case DatabaseValueContent content:
                WriteContent(writer, content);
                return;
            case byte[] bytes:
                writer.Write("0x");
                WriteHex(writer, bytes);
                return;
            case JsonElement element:
                WriteCompactJsonElement(writer, element);
                return;
            case Array array:
                writer.Write('[');
                var index = 0;
                foreach (var item in array)
                {
                    if (index++ > 0)
                    {
                        writer.Write(", ");
                    }

                    if (item is null)
                    {
                        writer.Write("NULL");
                    }
                    else
                    {
                        WriteFullValue(writer, item);
                    }
                }

                writer.Write(']');
                return;
            default:
                writer.Write(FormatScalar(value));
                return;
        }
    }

    private static void WriteHex(TextWriter writer, ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var count = Math.Min(BinaryChunkBytes, bytes.Length);
            writer.Write(Convert.ToHexString(bytes[..count]));
            bytes = bytes[count..];
        }
    }

    private static string FormatScalar(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static IReadOnlyList<int> JsonPropertyOrdinals(
        IReadOnlyList<DatabaseResultColumnViewModel> columns)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ordinals = new int[columns.Count];
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (!names.Add(columns[ordinal].Name))
            {
                throw new InvalidDataException(
                    $"JSON export requires unique column names. "
                    + $"Column '{columns[ordinal].Name}' appears more than once; use CSV instead.");
            }

            ordinals[ordinal] = ordinal;
        }

        return ordinals;
    }

    private static void WriteJsonRow(
        TextWriter writer,
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        DatabaseResultRowViewModel row,
        int depth)
    {
        writer.Write('{');
        var propertyIndex = 0;
        foreach (var ordinal in JsonPropertyOrdinals(columns))
        {
            writer.Write(propertyIndex++ == 0
                ? Environment.NewLine
                : $",{Environment.NewLine}");
            WriteJsonIndent(writer, depth + 1);
            WriteJsonString(writer, columns[ordinal].Name);
            writer.Write(": ");
            WriteJsonCell(writer, columns[ordinal], row.Cells[ordinal], depth + 1);
        }

        if (propertyIndex > 0)
        {
            writer.Write(Environment.NewLine);
            WriteJsonIndent(writer, depth);
        }

        writer.Write('}');
    }

    private static void WriteJsonCell(
        TextWriter writer,
        DatabaseResultColumnViewModel column,
        DatabaseResultCellViewModel cell,
        int depth)
    {
        if (cell.IsNull)
        {
            writer.Write("null");
            return;
        }

        if (cell.IsDefault)
        {
            WriteJsonString(writer, "DEFAULT");
            return;
        }

        if (cell.RawValue is DatabaseValueContent content)
        {
            WriteJsonContent(writer, content, depth);
            return;
        }

        if (column.ValueKind == DatabaseValueKind.Json
            && cell.RawValue is string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                WriteJsonElement(writer, document.RootElement, depth);
                return;
            }
            catch (JsonException)
            {
                WriteJsonCellAsString(writer, cell);
                return;
            }
        }

        if (cell.RawValue is string text
            && DatabaseResultCellViewModel.TryParse(
                text,
                column.ValueKind,
                out var parsed,
                out _))
        {
            WriteJsonValue(writer, parsed, depth);
            return;
        }

        if (column.ValueKind is DatabaseValueKind.Other or DatabaseValueKind.Network)
        {
            WriteJsonCellAsString(writer, cell);
            return;
        }

        if (cell.RawValue is { } rawValue)
        {
            WriteJsonValue(writer, rawValue, depth);
            return;
        }

        WriteJsonCellAsString(writer, cell);
    }

    private static void WriteJsonCellAsString(
        TextWriter writer,
        DatabaseResultCellViewModel cell)
    {
        if (cell.Column.ValueKind == DatabaseValueKind.Other)
        {
            WriteJsonString(writer, cell.Text);
            return;
        }

        if (cell.RawValue is string text)
        {
            WriteJsonString(writer, text);
            return;
        }

        WriteJsonString(
            writer,
            cell.RawValue is { } value ? FormatScalar(value) : cell.EditText);
    }

    private static void WriteJsonValue(TextWriter writer, object? value, int depth)
    {
        switch (value)
        {
            case DatabaseValueContent content:
                WriteJsonContent(writer, content, depth);
                return;
            case null:
                writer.Write("null");
                return;
            case JsonElement element:
                WriteJsonElement(writer, element, depth);
                return;
            case string text:
                WriteJsonString(writer, text);
                return;
            case char character:
                WriteJsonString(writer, character.ToString());
                return;
            case bool flag:
                writer.Write(flag ? "true" : "false");
                return;
            case byte[] bytes:
                WriteBase64JsonString(writer, bytes);
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong
                or decimal or Int128 or UInt128 or BigInteger:
                writer.Write(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return;
            case float number when float.IsFinite(number):
                writer.Write(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            case double number when double.IsFinite(number):
                writer.Write(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            case Half number when Half.IsFinite(number):
                writer.Write(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            case float or double or Half:
                throw new ArgumentException("Non-finite floating-point values cannot be exported as JSON.");
            case DateTime dateTime:
                WriteJsonString(writer, dateTime.ToString("O", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset dateTimeOffset:
                WriteJsonString(writer, dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                return;
            case DateOnly date:
                WriteJsonString(writer, date.ToString("O", CultureInfo.InvariantCulture));
                return;
            case TimeOnly time:
                WriteJsonString(writer, time.ToString("O", CultureInfo.InvariantCulture));
                return;
            case TimeSpan duration:
                WriteJsonString(writer, duration.ToString("c", CultureInfo.InvariantCulture));
                return;
            case Guid guid:
                WriteJsonString(writer, guid.ToString());
                return;
            case Enum enumeration:
                writer.Write(Convert.ToInt64(enumeration, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture));
                return;
            case Array values:
                writer.Write('[');
                var index = 0;
                foreach (var item in values)
                {
                    writer.Write(index++ == 0
                        ? Environment.NewLine
                        : $",{Environment.NewLine}");
                    WriteJsonIndent(writer, depth + 1);
                    WriteJsonValue(writer, item, depth + 1);
                }

                if (index > 0)
                {
                    writer.Write(Environment.NewLine);
                    WriteJsonIndent(writer, depth);
                }

                writer.Write(']');
                return;
            default:
                WriteJsonString(writer, FormatScalar(value));
                return;
        }
    }

    private static void WriteJsonElement(
        TextWriter writer,
        JsonElement element,
        int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.Write('{');
                var propertyIndex = 0;
                foreach (var property in element.EnumerateObject())
                {
                    writer.Write(propertyIndex++ == 0
                        ? Environment.NewLine
                        : $",{Environment.NewLine}");
                    WriteJsonIndent(writer, depth + 1);
                    WriteJsonString(writer, property.Name);
                    writer.Write(": ");
                    WriteJsonElement(writer, property.Value, depth + 1);
                }

                if (propertyIndex > 0)
                {
                    writer.Write(Environment.NewLine);
                    WriteJsonIndent(writer, depth);
                }

                writer.Write('}');
                return;
            case JsonValueKind.Array:
                writer.Write('[');
                var itemIndex = 0;
                foreach (var item in element.EnumerateArray())
                {
                    writer.Write(itemIndex++ == 0
                        ? Environment.NewLine
                        : $",{Environment.NewLine}");
                    WriteJsonIndent(writer, depth + 1);
                    WriteJsonElement(writer, item, depth + 1);
                }

                if (itemIndex > 0)
                {
                    writer.Write(Environment.NewLine);
                    WriteJsonIndent(writer, depth);
                }

                writer.Write(']');
                return;
            case JsonValueKind.String:
                WriteJsonString(writer, element.GetString() ?? string.Empty);
                return;
            case JsonValueKind.Number:
                writer.Write(element.GetRawText());
                return;
            case JsonValueKind.True:
                writer.Write("true");
                return;
            case JsonValueKind.False:
                writer.Write("false");
                return;
            case JsonValueKind.Null:
                writer.Write("null");
                return;
            default:
                throw new InvalidDataException("The JSON value is undefined.");
        }
    }

    private static void WriteCompactJsonElement(TextWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.Write('{');
                var propertyIndex = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (propertyIndex++ > 0)
                    {
                        writer.Write(',');
                    }

                    WriteJsonString(writer, property.Name);
                    writer.Write(':');
                    WriteCompactJsonElement(writer, property.Value);
                }

                writer.Write('}');
                return;
            case JsonValueKind.Array:
                writer.Write('[');
                var itemIndex = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (itemIndex++ > 0)
                    {
                        writer.Write(',');
                    }

                    WriteCompactJsonElement(writer, item);
                }

                writer.Write(']');
                return;
            case JsonValueKind.String:
                WriteJsonString(writer, element.GetString() ?? string.Empty);
                return;
            case JsonValueKind.Number:
                writer.Write(element.GetRawText());
                return;
            case JsonValueKind.True:
                writer.Write("true");
                return;
            case JsonValueKind.False:
                writer.Write("false");
                return;
            case JsonValueKind.Null:
                writer.Write("null");
                return;
            default:
                throw new InvalidDataException("The JSON value is undefined.");
        }
    }

    private static void WriteBase64JsonString(TextWriter writer, ReadOnlySpan<byte> bytes)
    {
        writer.Write('"');
        while (!bytes.IsEmpty)
        {
            var count = Math.Min(Base64ChunkBytes, bytes.Length);
            writer.Write(Convert.ToBase64String(bytes[..count]));
            bytes = bytes[count..];
        }

        writer.Write('"');
    }

    private static void WriteJsonString(TextWriter writer, string value)
    {
        writer.Write('"');
        var segmentStart = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            string? escape = character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when character < ' ' => $"\\u{(int)character:X4}",
                _ when char.IsHighSurrogate(character)
                    && (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) =>
                    "\\uFFFD",
                _ when char.IsLowSurrogate(character)
                    && (index == 0 || !char.IsHighSurrogate(value[index - 1])) =>
                    "\\uFFFD",
                _ => null,
            };
            if (escape is null)
            {
                continue;
            }

            writer.Write(value.AsSpan(segmentStart, index - segmentStart));
            writer.Write(escape);
            segmentStart = index + 1;
        }

        writer.Write(value.AsSpan(segmentStart));
        writer.Write('"');
    }

    private static void WriteJsonIndent(TextWriter writer, int depth)
    {
        for (var index = 0; index < depth; index++)
        {
            writer.Write("  ");
        }
    }

    private static void WriteJsonCell(
        Utf8JsonWriter writer,
        DatabaseResultColumnViewModel column,
        DatabaseResultCellViewModel cell)
    {
        if (cell.IsNull)
        {
            writer.WriteNullValue();
            return;
        }

        if (cell.IsDefault)
        {
            writer.WriteStringValue("DEFAULT");
            return;
        }

        if (cell.RawValue is DatabaseValueContent)
        {
            throw new NotSupportedException("Detached database values require the streaming TextWriter JSON export.");
        }

        if (column.ValueKind == DatabaseValueKind.Json
            && cell.RawValue is string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                document.RootElement.WriteTo(writer);
                return;
            }
            catch (JsonException)
            {
                WriteJsonCellAsString(writer, cell);
                return;
            }
        }

        if (cell.RawValue is string text
            && DatabaseResultCellViewModel.TryParse(
                text,
                column.ValueKind,
                out var parsed,
                out _))
        {
            WriteJsonValue(writer, parsed);
            return;
        }

        if (column.ValueKind is DatabaseValueKind.Other or DatabaseValueKind.Network)
        {
            WriteJsonCellAsString(writer, cell);
            return;
        }

        if (cell.RawValue is { } rawValue)
        {
            WriteJsonValue(writer, rawValue);
            return;
        }

        WriteJsonCellAsString(writer, cell);
    }

    private static void WriteJsonCellAsString(
        Utf8JsonWriter writer,
        DatabaseResultCellViewModel cell)
    {
        if (cell.Column.ValueKind == DatabaseValueKind.Other)
        {
            writer.WriteStringValue(cell.Text);
            return;
        }

        if (cell.RawValue is string text)
        {
            writer.WriteStringValue(text);
            return;
        }

        using var content = new StringWriter(CultureInfo.InvariantCulture);
        WriteCellText(content, cell);
        writer.WriteStringValue(content.ToString());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case DatabaseValueContent:
                throw new NotSupportedException("Detached database values require the streaming TextWriter JSON export.");
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case char character:
                writer.WriteStringValue(character.ToString());
                return;
            case bool flag:
                writer.WriteBooleanValue(flag);
                return;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                return;
            case sbyte number:
                writer.WriteNumberValue(number);
                return;
            case byte number:
                writer.WriteNumberValue(number);
                return;
            case short number:
                writer.WriteNumberValue(number);
                return;
            case ushort number:
                writer.WriteNumberValue(number);
                return;
            case int number:
                writer.WriteNumberValue(number);
                return;
            case uint number:
                writer.WriteNumberValue(number);
                return;
            case long number:
                writer.WriteNumberValue(number);
                return;
            case ulong number:
                writer.WriteNumberValue(number);
                return;
            case decimal number:
                writer.WriteNumberValue(number);
                return;
            case float number:
                writer.WriteNumberValue(number);
                return;
            case double number:
                writer.WriteNumberValue(number);
                return;
            case Half number:
                writer.WriteNumberValue((float)number);
                return;
            case Int128 number:
                writer.WriteRawValue(number.ToString(CultureInfo.InvariantCulture));
                return;
            case UInt128 number:
                writer.WriteRawValue(number.ToString(CultureInfo.InvariantCulture));
                return;
            case BigInteger number:
                writer.WriteRawValue(number.ToString(CultureInfo.InvariantCulture));
                return;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                return;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                return;
            case DateOnly date:
                writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture));
                return;
            case TimeOnly time:
                writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture));
                return;
            case TimeSpan duration:
                writer.WriteStringValue(duration.ToString("c", CultureInfo.InvariantCulture));
                return;
            case Guid guid:
                writer.WriteStringValue(guid);
                return;
            case Array values:
                writer.WriteStartArray();
                foreach (var item in values)
                {
                    WriteJsonValue(writer, item);
                }

                writer.WriteEndArray();
                return;
            default:
                // Provider-specific CLR values have no stable JSON schema. A
                // deterministic invariant string is the only portable shape;
                // runtime-type serialization would require reflection and can
                // silently change when a provider updates.
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }

    private static void WriteSqlLiteral(
        TextWriter writer,
        DatabaseResultColumnViewModel column,
        DatabaseResultCellViewModel cell)
    {
        if (cell.IsDefault)
        {
            writer.Write("DEFAULT");
            return;
        }

        if (cell.IsNull)
        {
            writer.Write("NULL");
            return;
        }

        var value = cell.RawValue;
        if (value is string text
            && DatabaseResultCellViewModel.TryParse(
                text,
                column.ValueKind,
                out var parsed,
                out _))
        {
            value = parsed;
        }

        switch (value)
        {
            case DatabaseValueContent { Kind: DatabaseValueKind.Binary } content:
                writer.Write("X'");
                using (var source = content.OpenRead())
                {
                    WriteBinaryContent(writer, source, asBase64: false);
                }
                writer.Write('\'');
                return;
            case DatabaseValueContent { ScalarType: not null } content:
                WriteContent(writer, content);
                return;
            case bool flag:
                writer.Write(flag ? "TRUE" : "FALSE");
                return;
            case byte[] bytes:
                writer.Write("X'");
                WriteHex(writer, bytes);
                writer.Write('\'');
                return;
            case IFormattable formattable when column.ValueKind is
                DatabaseValueKind.SignedInteger
                or DatabaseValueKind.UnsignedInteger
                or DatabaseValueKind.Decimal
                or DatabaseValueKind.FloatingPoint:
                writer.Write(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            default:
                writer.Write('\'');
                var escapingWriter = new CharacterEscapingTextWriter(writer, '\'');
                WriteCellText(escapingWriter, cell);
                writer.Write('\'');
                return;
        }
    }

    private static void WriteQuotedIdentifier(TextWriter writer, string identifier)
    {
        writer.Write('"');
        WriteEscaped(writer, identifier, '"');
        writer.Write('"');
    }

    private static void WriteEscaped(TextWriter writer, string value, char character)
    {
        var start = 0;
        while (true)
        {
            var index = value.IndexOf(character, start);
            if (index < 0)
            {
                writer.Write(value.AsSpan(start));
                return;
            }

            writer.Write(value.AsSpan(start, index - start + 1));
            writer.Write(character);
            start = index + 1;
        }
    }

    private static void ValidateRowWidth(
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        DatabaseResultRowViewModel row)
    {
        if (row.Cells.Count != columns.Count)
        {
            throw new ArgumentException(
                "The row does not match the current result columns.",
                nameof(row));
        }
    }

    private sealed class CharacterEscapingTextWriter(TextWriter inner, char character)
        : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            if (value == character)
            {
                inner.Write(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is not null)
            {
                WriteEscaped(inner, value, character);
            }
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            var start = 0;
            while (true)
            {
                var index = buffer[start..].IndexOf(character);
                if (index < 0)
                {
                    inner.Write(buffer[start..]);
                    return;
                }

                index += start;
                inner.Write(buffer[start..(index + 1)]);
                inner.Write(character);
                start = index + 1;
            }
        }
    }

    private sealed class BoundedUtf8StringWriter(int maximumBytes, CancellationToken token) : TextWriter
    {
        private readonly StringBuilder _content = new();
        private int _utf8Bytes;

        public override Encoding Encoding => Utf8WithoutBom;

        public override void Write(char value)
        {
            Span<char> buffer = [value];
            AddBytes(Utf8WithoutBom.GetByteCount(buffer));
            _content.Append(value);
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            AddBytes(Utf8WithoutBom.GetByteCount(value));
            _content.Append(value);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            if (buffer.IsEmpty)
            {
                return;
            }

            AddBytes(Utf8WithoutBom.GetByteCount(buffer));
            _content.Append(buffer);
        }

        public override string ToString() => _content.ToString();

        private void AddBytes(int count)
        {
            token.ThrowIfCancellationRequested();
            if (count > maximumBytes - _utf8Bytes)
            {
                throw ClipboardLimitExceeded();
            }

            _utf8Bytes += count;
        }
    }

    private static InvalidDataException ClipboardLimitExceeded() => new(
        "Clipboard output exceeds the 16 MiB UTF-8 limit. Export the current page to a file instead.");
}
