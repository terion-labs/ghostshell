using System.Text;
using System.Text.RegularExpressions;

namespace Asura.AccessibilityAcceptance;

internal sealed record SanitizedText(string Value, int RedactionsApplied);

internal static partial class EvidenceSanitizer
{
    public const int MaximumNoteLength = 2_048;

    public static SanitizedText SanitizeNote(string? value)
    {
        var text = NormalizeSingleLine(value);
        var redactions = 0;
        text = Replace(PrivateKey(), text, "[PRIVATE_KEY_REDACTED]", ref redactions);
        text = Replace(Url(), text, "[URL_REDACTED]", ref redactions);
        text = Replace(Authorization(), text, "$1=[SECRET_REDACTED]", ref redactions);
        text = Replace(BearerToken(), text, "Bearer [SECRET_REDACTED]", ref redactions);
        text = Replace(Email(), text, "[EMAIL_REDACTED]", ref redactions);
        text = Replace(Ipv4(), text, "[ADDRESS_REDACTED]", ref redactions);
        text = Replace(Ipv6(), text, "[ADDRESS_REDACTED]", ref redactions);
        text = Replace(WindowsAbsolutePath(), text, "[PATH_REDACTED]", ref redactions);
        text = Replace(UnixAbsolutePath(), text, "[PATH_REDACTED]", ref redactions);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var homePattern = new Regex(
                Regex.Escape(home) + @"(?:[/\\][^\s,;]*)?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            text = Replace(homePattern, text, "[PATH_REDACTED]", ref redactions);
        }

        if (text.Length > MaximumNoteLength)
        {
            text = text[..MaximumNoteLength].TrimEnd() + " [TRUNCATED]";
            redactions++;
        }

        return new SanitizedText(text, redactions);
    }

    public static SanitizedText SanitizeSingleLine(string? value)
    {
        var text = NormalizeSingleLine(value);
        if (text.Length <= MaximumNoteLength)
        {
            return new SanitizedText(text, 0);
        }

        return new SanitizedText(
            text[..MaximumNoteLength].TrimEnd() + " [TRUNCATED]",
            1);
    }

    public static string SanitizeIdentifier(string? value)
    {
        var candidate = NormalizeSingleLine(value);
        if (candidate.Length is < 3 or > 64
            || !Identifier().IsMatch(candidate))
        {
            return "redacted-identifier";
        }

        return candidate;
    }

    public static bool IsValidIdentifier(string? value) =>
        value is { Length: >= 3 and <= 64 }
        && Identifier().IsMatch(value);

    public static bool IsHostFingerprint(string? value) =>
        value is not null && HostFingerprint().IsMatch(value);

    public static bool IsSanitizedNote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sanitized = SanitizeNote(value);
        return string.Equals(value, sanitized.Value, StringComparison.Ordinal)
            && value.Length is >= 12 and <= MaximumNoteLength + 12;
    }

    public static bool IsSafeSingleLine(string value, int minimumLength, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (minimumLength < 0 || maximumLength < minimumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLength));
        }

        return value.Length >= minimumLength
            && value.Length <= maximumLength
            && string.Equals(
                value,
                SanitizeNote(value).Value,
                StringComparison.Ordinal);
    }

    public static bool IsBoundedSingleLine(string value, int minimumLength, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (minimumLength < 0 || maximumLength < minimumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLength));
        }

        return value.Length >= minimumLength
            && value.Length <= maximumLength
            && string.Equals(value, NormalizeSingleLine(value), StringComparison.Ordinal);
    }

    public static bool IsSafeVersionText(string value) =>
        value.Length is >= 1 and <= 256
        && VersionText().IsMatch(value);

    private static string NormalizeSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            var safe = char.IsControl(character) || char.IsWhiteSpace(character)
                ? ' '
                : character;
            if (safe == ' ')
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                previousWasWhitespace = true;
            }
            else
            {
                previousWasWhitespace = false;
            }

            builder.Append(safe);
        }

        return builder.ToString().Trim();
    }

    private static string Replace(
        Regex pattern,
        string value,
        string replacement,
        ref int redactions)
    {
        var matches = pattern.Count(value);
        if (matches == 0)
        {
            return value;
        }

        redactions += matches;
        return pattern.Replace(value, replacement);
    }

    [GeneratedRegex(
        @"^[A-Za-z0-9][A-Za-z0-9._-]*$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Identifier();

    [GeneratedRegex(
        @"^host-[0-9a-f]{16}$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex HostFingerprint();

    [GeneratedRegex(
        @"^[A-Za-z0-9][A-Za-z0-9 ._+()~-]*$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex VersionText();

    [GeneratedRegex(
        @"-----BEGIN [^-\r\n]*PRIVATE KEY-----.*?-----END [^-\r\n]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(
        @"\b(?:https?|ftp)://[^\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Url();

    [GeneratedRegex(
        "\\b(token|secret|password|passwd|api[_-]?key|authorization)\\s*[:=]\\s*(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Authorization();

    [GeneratedRegex(
        @"\bBearer\s+[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex BearerToken();

    [GeneratedRegex(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Email();

    [GeneratedRegex(
        @"(?<![A-Fa-f0-9:])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![A-Fa-f0-9:])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Ipv4();

    [GeneratedRegex(
        @"(?<![A-Fa-f0-9:])(?:[A-Fa-f0-9]{0,4}:){2,7}[A-Fa-f0-9]{0,4}(?![A-Fa-f0-9:])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Ipv6();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:[A-Za-z]:\\|\\\\)[^\s,;]+",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex WindowsAbsolutePath();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9.])/(?:[^\s,;]+)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex UnixAbsolutePath();
}
