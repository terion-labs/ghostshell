using System.Globalization;
using System.Text;

namespace Asura.App.ViewModels;

/// <summary>
/// The bounded CSV interchange used by the database grid. Parsing is kept
/// separate from row staging so malformed input cannot leave half an import in
/// the editor.
/// </summary>
internal static class DatabaseGridCsv
{
    public const int MaximumCharacters = 16 * 1024 * 1024;
    public const int MaximumRows = 5000;
    public const int MaximumColumns = 4096;
    public const int MaximumHeaderCharacters = 4096;
    public const int MaximumStagedCells = 100_000;

    public static void ValidateStagingSize(int rowCount, int tableColumnCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(tableColumnCount, 1);
        if ((long)rowCount * tableColumnCount > MaximumStagedCells)
        {
            throw new InvalidDataException(
                $"CSV imports are limited to {MaximumStagedCells.ToString("N0", CultureInfo.InvariantCulture)} staged cells. "
                + "Import fewer rows or use a narrower table projection.");
        }
    }

    public static DatabaseGridCsvDocument Parse(
        string text,
        int maximumColumns = MaximumColumns)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumColumns is < 1 or > MaximumColumns)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumColumns),
                $"CSV column limits must be between 1 and {MaximumColumns}.");
        }

        if (text.Length > MaximumCharacters)
        {
            throw new InvalidDataException("CSV imports are limited to 16 MiB of text.");
        }

        var rows = ParseRows(text.TrimStart('\uFEFF'), maximumColumns);
        if (rows.Count == 0)
        {
            throw new InvalidDataException("The CSV file has no header row.");
        }

        if (rows.Count > MaximumRows + 1)
        {
            throw new InvalidDataException($"CSV imports are limited to {MaximumRows} data rows.");
        }

        var headers = rows[0].ToArray();
        if (headers.Length == 0 || headers.Any(header => header.Length == 0))
        {
            throw new InvalidDataException("Every CSV column must have a header.");
        }

        var duplicates = headers
            .GroupBy(header => header, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            var shown = string.Join(
                ", ",
                duplicates.Take(5).Select(DescribeHeader));
            var remainder = duplicates.Length > 5
                ? $" (+{duplicates.Length - 5} more)"
                : string.Empty;
            throw new InvalidDataException(
                $"CSV headers must be unique. Repeated: {shown}{remainder}.");
        }

        var data = new List<IReadOnlyList<string>>(Math.Max(0, rows.Count - 1));
        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.Count != headers.Length)
            {
                throw new InvalidDataException(
                    $"CSV row {rowIndex + 1} has {row.Count} values; expected {headers.Length}.");
            }

            data.Add(row);
        }

        return new DatabaseGridCsvDocument(headers, data);
    }

    internal static string DescribeHeader(string header)
    {
        const int maximumShownCharacters = 128;
        var oneLine = header
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        return oneLine.Length <= maximumShownCharacters
            ? oneLine
            : oneLine[..(maximumShownCharacters - 1)] + "…";
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseRows(
        string text,
        int maximumColumns)
    {
        var rows = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var afterQuote = false;
        var fieldStarted = false;
        var rowStarted = false;

        void EndField()
        {
            if (fields.Count >= maximumColumns)
            {
                throw new InvalidDataException(
                    $"CSV rows are limited to {maximumColumns} columns.");
            }

            if (rows.Count == 0 && field.Length > MaximumHeaderCharacters)
            {
                throw new InvalidDataException(
                    $"CSV headers are limited to {MaximumHeaderCharacters} characters each.");
            }

            fields.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
            afterQuote = false;
        }

        void EndRow()
        {
            EndField();
            rows.Add([.. fields]);
            fields.Clear();
            rowStarted = false;
            if (rows.Count > MaximumRows + 1)
            {
                throw new InvalidDataException(
                    $"CSV imports are limited to {MaximumRows} data rows.");
            }
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character != '"')
                {
                    field.Append(character);
                    continue;
                }

                if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                    continue;
                }

                inQuotes = false;
                afterQuote = true;
                continue;
            }

            if (afterQuote && character is not (',' or '\r' or '\n'))
            {
                throw new InvalidDataException(
                    $"CSV contains unexpected text after a closing quote at character {index + 1}.");
            }

            switch (character)
            {
                case '"' when !fieldStarted && field.Length == 0:
                    inQuotes = true;
                    fieldStarted = true;
                    rowStarted = true;
                    break;
                case '"':
                    throw new InvalidDataException(
                        $"CSV contains a quote inside an unquoted field at character {index + 1}.");
                case ',':
                    EndField();
                    rowStarted = true;
                    break;
                case '\r':
                    EndRow();
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    field.Append(character);
                    fieldStarted = true;
                    rowStarted = true;
                    break;
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("CSV ends inside a quoted field.");
        }

        if (rowStarted || fieldStarted || afterQuote || fields.Count > 0)
        {
            EndRow();
        }

        return rows;
    }

    public static string Format(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        if (headers.Count == 0)
        {
            return string.Empty;
        }

        var output = new StringBuilder();
        AppendRow(output, headers);
        foreach (var row in rows)
        {
            if (row.Count != headers.Count)
            {
                throw new ArgumentException(
                    "Every exported row must match the header width.",
                    nameof(rows));
            }

            AppendRow(output, row);
        }

        return output.ToString();
    }

    private static void AppendRow(StringBuilder output, IReadOnlyList<string?> fields)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (index > 0)
            {
                output.Append(',');
            }

            var field = fields[index] ?? string.Empty;
            if (field.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            {
                output.Append('"');
                output.Append(field.Replace("\"", "\"\"", StringComparison.Ordinal));
                output.Append('"');
            }
            else
            {
                output.Append(field);
            }
        }

        output.AppendLine();
    }
}

internal sealed record DatabaseGridCsvDocument(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows);
