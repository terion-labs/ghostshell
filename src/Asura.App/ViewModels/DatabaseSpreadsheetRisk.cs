using Asura.Application;

namespace Asura.App.ViewModels;

/// <summary>Advisory only: raw interchange values are never rewritten.</summary>
internal static class DatabaseSpreadsheetRisk
{
    public const string Notice = "This data contains formula-like cells or headings. Spreadsheet apps may execute them when pasted or imported. Values are unchanged; review them before opening in a spreadsheet.";

    public static bool ContainsFormula(
        IEnumerable<DatabaseResultCellViewModel> cells,
        IEnumerable<string>? headings = null,
        CancellationToken cancellationToken = default) =>
        (headings?.Any(text => IsFormula(text, cancellationToken)) == true)
            || cells.Any(cell => IsFormula(cell, cancellationToken));

    private static bool IsFormula(DatabaseResultCellViewModel cell, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (cell.IsNull || cell.IsDefault)
        {
            return false;
        }

        if (cell.RawValue is DatabaseValueContent { Kind: DatabaseValueKind.Text or DatabaseValueKind.Json } content)
        {
            using var reader = new StreamReader(content.OpenRead());
            return IsFormula(reader, token);
        }

        return cell.RawValue is string text
            ? IsFormula(text, token)
            : cell.Column.ValueKind == DatabaseValueKind.Other && IsFormula(cell.Text, token);
    }

    internal static bool IsFormula(string text) => IsFormula(text, CancellationToken.None);

    private static bool IsFormula(string text, CancellationToken token)
    {
        using var reader = new StringReader(text);
        return IsFormula(reader, token);
    }

    private static bool IsFormula(TextReader reader, CancellationToken token)
    {
        int character;
        while ((character = reader.Read()) >= 0)
        {
            token.ThrowIfCancellationRequested();
            var value = (char)character;
            if (char.IsWhiteSpace(value) || char.IsControl(value) || value == '\uFEFF')
            {
                continue;
            }

            return value is '=' or '+' or '-' or '@';
        }

        return false;
    }
}
