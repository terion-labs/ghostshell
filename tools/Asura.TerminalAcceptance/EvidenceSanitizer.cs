using System.Text;
using System.Text.RegularExpressions;

namespace Asura.TerminalAcceptance;

internal sealed record SanitizedText(string Value, int RedactionsApplied);

internal static partial class EvidenceSanitizer
{
    public const int MaximumNoteLength = 2_000;
    private const int RegexTimeoutMilliseconds = 1_000;

    public static SanitizedText SanitizeNote(string value) =>
        Sanitize(value, MaximumNoteLength);

    public static SanitizedText SanitizeSingleLine(string value) =>
        Sanitize(value, 500);

    public static string SanitizeIdentifier(string value)
    {
        var sanitized = UnsafeIdentifierCharacter().Replace(value.Trim(), "_").Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized[..Math.Min(64, sanitized.Length)];
    }

    public static bool IsSanitizedNote(string value) =>
        string.Equals(SanitizeNote(value).Value, value, StringComparison.Ordinal);

    public static bool IsSafeIdentifier(string value) =>
        SafeIdentifier().IsMatch(value);

    private static SanitizedText Sanitize(string value, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);

        var redactions = 0;
        var normalized = NormalizeWhitespace(value);
        normalized = ReplaceAndCount(
            AuthorizationCredential(),
            normalized,
            match => $"{match.Groups["name"].Value}=[REDACTED]",
            ref redactions);
        normalized = ReplaceAndCount(
            SecretAssignment(),
            normalized,
            match => $"{match.Groups["name"].Value}=[REDACTED]",
            ref redactions);
        normalized = ReplaceAndCount(
            BearerCredential(),
            normalized,
            _ => "Bearer [REDACTED]",
            ref redactions);
        normalized = ReplaceAndCount(
            NetworkUrl(),
            normalized,
            _ => "[URL_REDACTED]",
            ref redactions);
        normalized = ReplaceAndCount(
            PrivateKeyMaterial(),
            normalized,
            _ => "[PRIVATE_KEY_REDACTED]",
            ref redactions);
        normalized = ReplaceAndCount(
            EmailAddress(),
            normalized,
            _ => "[EMAIL_REDACTED]",
            ref redactions);
        normalized = ReplaceAndCount(
            Ipv6Address(),
            normalized,
            _ => "[IP_REDACTED]",
            ref redactions);
        normalized = ReplaceAndCount(
            Ipv4Address(),
            normalized,
            _ => "[IP_REDACTED]",
            ref redactions);
        normalized = ReplaceAndCount(
            UserHomePath(),
            normalized,
            _ => "[HOME]",
            ref redactions);
        normalized = ReplaceKnownHomeDirectory(normalized, ref redactions);
        normalized = ReplaceAndCount(
            WindowsAbsolutePath(),
            normalized,
            _ => "[PATH_REDACTED]",
            ref redactions);
        normalized = ReplaceAndCount(
            UnixAbsolutePath(),
            normalized,
            _ => "[PATH_REDACTED]",
            ref redactions);

        if (normalized.Length > maximumLength)
        {
            normalized = normalized[..maximumLength].TrimEnd() + " [TRUNCATED]";
            redactions++;
        }

        return new SanitizedText(normalized, redactions);
    }

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = true;
        foreach (var character in value)
        {
            var isWhitespace = char.IsWhiteSpace(character) || char.IsControl(character);
            if (isWhitespace)
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(character);
            }

            previousWasWhitespace = isWhitespace;
        }

        return builder.ToString().Trim();
    }

    private static string ReplaceAndCount(
        Regex pattern,
        string value,
        MatchEvaluator replacement,
        ref int redactions)
    {
        var count = pattern.Count(value);
        if (count == 0)
        {
            return value;
        }

        redactions += count;
        return pattern.Replace(value, replacement);
    }

    private static string ReplaceKnownHomeDirectory(string value, ref int redactions)
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("HOME"),
            Environment.GetEnvironmentVariable("USERPROFILE"),
        };

        foreach (var candidate in candidates
                     .Where(candidate => !string.IsNullOrWhiteSpace(candidate) && candidate.Length >= 4)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var index = value.IndexOf(candidate!, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                value = string.Concat(value.AsSpan(0, index), "[HOME]", value.AsSpan(index + candidate!.Length));
                redactions++;
                index = value.IndexOf(candidate, index + "[HOME]".Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        return value;
    }

    [GeneratedRegex("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex UnsafeIdentifierCharacter();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex SafeIdentifier();

    [GeneratedRegex(
        "\\b(?<name>authorization)\\s*[:=]\\s*(?:(?:basic|bearer)\\s+)?(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex AuthorizationCredential();

    [GeneratedRegex(
        "(?<name>password|passwd|pwd|token|api[-_ ]?key|secret|authorization|cookie|private[-_ ]?key)\\s*[:=]\\s*(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(
        "\\bBearer\\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex BearerCredential();

    [GeneratedRegex(
        "\\b(?:https?|ssh|ftp)://[^\\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex NetworkUrl();

    [GeneratedRegex(
        "-----BEGIN[^-]*PRIVATE KEY-----.*?-----END[^-]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex PrivateKeyMaterial();

    [GeneratedRegex(
        "\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex EmailAddress();

    [GeneratedRegex(
        "(?<![0-9A-F:])(?=[0-9A-F:.]*:[0-9A-F:.]*:)[0-9A-F:.]+(?![0-9A-F:])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex Ipv6Address();

    [GeneratedRegex(
        "(?<![0-9.])(?:[0-9]{1,3}\\.){3}[0-9]{1,3}(?![0-9.])",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex Ipv4Address();

    [GeneratedRegex(
        "(?:[A-Za-z]:\\\\Users\\\\[^\\s\\\\/]+|/(?:Users|home)/[^\\s/]+)(?:[/\\\\][^\\s|;,]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex UserHomePath();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])(?:[A-Za-z]:[\\\\/]|\\\\\\\\)[^\\s|;,]+",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex WindowsAbsolutePath();

    [GeneratedRegex(
        "(?<![A-Za-z0-9:/])/(?!/)[^\\s|;,]+",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex UnixAbsolutePath();
}
