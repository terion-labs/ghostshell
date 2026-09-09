using System.Globalization;
using System.Text;
using Avalonia.Data.Converters;

namespace Asura.App.Converters;

/// <summary>
/// Renders enum options as prose. Option lists bind straight to enum values, so
/// without this a menu reads <c>DiscardAndShowHint</c> or <c>ConfirmBeforeOpen</c>
/// — the identifier rather than the choice. Non-enum values pass through
/// untouched so lists of profiles and descriptors keep their own text.
/// </summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public static EnumDisplayConverter Instance { get; } = new();

    /// <summary>
    /// Names that carry a proper spelling of their own. Splitting these on case
    /// boundaries would produce "Open ai" for a vendor written "OpenAI".
    /// </summary>
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.Ordinal)
    {
        ["OpenAi"] = "OpenAI",
        ["OpenAiCompatible"] = "OpenAI-compatible",
    };

    /// <summary>
    /// Tokens that are initialisms rather than words, kept upper-case so they do
    /// not become "Ime" or "Ssh".
    /// </summary>
    private static readonly HashSet<string> Initialisms = new(StringComparer.OrdinalIgnoreCase)
    {
        "IME", "SSH", "WSL", "OSC", "PTY", "UI", "OS", "ID", "URL", "URI", "API", "TTY", "ANSI",
        "AI", "MCP", "SFTP", "FTP", "SMB", "S3", "TLS", "DNS",
    };

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        value is Enum ? Humanize(value.ToString()) : value?.ToString();

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException(
            "Enum display text is one-way; bind SelectedItem to the enum value itself.");

    private static string Humanize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        if (Overrides.TryGetValue(name, out var exact))
        {
            return exact;
        }

        var words = SplitWords(name);
        var builder = new StringBuilder(name.Length + 8);
        for (var index = 0; index < words.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            var word = words[index];
            if (Initialisms.Contains(word))
            {
                builder.Append(word.ToUpperInvariant());
            }
            else if (index == 0)
            {
                builder.Append(char.ToUpperInvariant(word[0])).Append(word[1..].ToLowerInvariant());
            }
            else
            {
                builder.Append(word.ToLowerInvariant());
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits on lower-to-upper transitions and on the last capital of a run, so
    /// "ConfirmBeforeOpen" yields three words and "OSCPolicy" yields "OSC" and
    /// "Policy" rather than one run.
    /// </summary>
    private static List<string> SplitWords(string name)
    {
        var words = new List<string>(4);
        var start = 0;
        for (var index = 1; index < name.Length; index++)
        {
            var isBoundary = char.IsUpper(name[index])
                && (!char.IsUpper(name[index - 1])
                    || (index + 1 < name.Length && char.IsLower(name[index + 1])));
            if (isBoundary)
            {
                words.Add(name[start..index]);
                start = index;
            }
        }

        words.Add(name[start..]);
        return words;
    }
}
