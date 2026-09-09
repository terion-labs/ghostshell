using System.Text;

namespace Asura.Application.Previews;

/// <summary>
/// Separated values, read the way the format is actually written: quoted
/// fields may contain the separator, line breaks, and doubled quotes.
/// </summary>
public static class DelimitedText
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(
        string text,
        char separator,
        int maximumRows)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);

        var rows = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            any = true;
        }

        void EndRow()
        {
            EndField();
            rows.Add([.. fields]);
            fields.Clear();
            any = false;
        }

        for (var index = 0; index < text.Length && rows.Count < maximumRows; index++)
        {
            var character = text[index];
            if (quoted)
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

                quoted = false;
                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    if (character == separator)
                    {
                        EndField();
                    }
                    else
                    {
                        field.Append(character);
                    }

                    break;
            }
        }

        if (rows.Count < maximumRows && (any || field.Length > 0))
        {
            EndRow();
        }

        return rows;
    }
}
