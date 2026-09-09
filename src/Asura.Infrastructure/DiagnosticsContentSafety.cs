using System.Text;
using System.Text.RegularExpressions;

namespace Asura.Infrastructure;

internal enum DiagnosticsSafetyFailure
{
    None,
    InvalidText,
    UnsafeContent,
}

/// <summary>
/// Canonicalizes caller-supplied diagnostics text, redacts narrowly recognized secret assignments,
/// then rejects content whose safety cannot be established. This is a second boundary behind the
/// closed Application contract, not a substitute for keeping sensitive sources out of the request.
/// </summary>
internal static class DiagnosticsContentSafety
{
    private const string RedactionMarker = "[REDACTED]";

    // A ReDoS bound, not a latency budget: a timeout still fails closed as
    // unsafe, so it must sit far above scheduler noise — at 250ms, first-use
    // regex compilation on a saturated machine produced spurious rejections
    // of safe static content.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly string SensitiveNamePattern =
        "(?:password|passphrase|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|"
        + "session[_ -]?token|id[_ -]?token|token|bearer|client[_ -]?secret|"
        + "secret[_ -]?access[_ -]?key|secret(?:[_ -]?value)?|credential[_ -]?value|"
        + "authorization|proxy[_ -]?authorization|cookie|set-cookie|private[_ -]?key)";

    private static readonly Regex SensitiveStructuredJson = CreateRegex(
        $"[\"']{SensitiveNamePattern}[\"']\\s*:\\s*(?:\\{{|\\[)",
        RegexOptions.IgnoreCase);

    private static readonly Regex SensitiveJsonScalar = CreateRegex(
        $"[\"']{SensitiveNamePattern}[\"']\\s*:\\s*"
        + "(?:\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|[^,}\\]\\r\\n]+)",
        RegexOptions.IgnoreCase);

    private static readonly Regex SensitiveAssignment = CreateRegex(
        $"(?<![A-Za-z0-9_])[\"']?{SensitiveNamePattern}[\"']?\\s*[:=]",
        RegexOptions.IgnoreCase);

    private static readonly Regex ForbiddenField = CreateRegex(
        "(?<![A-Za-z0-9_])[\"']?(?:command(?:[_ -]?(?:text|line|history))?|arguments?|argv|"
        + "terminal(?:[_ -]?(?:content|output))?|transcript|scrollback|screen[_ -]?text|"
        + "stdin|stdout|stderr|shell[_ -]?(?:input|output|history)|environment(?:[_ -]?variables)?|"
        + "env[_ -]?dump|user(?:name|[_ -]?id)?|login|credentials?)[\"']?\\s*[:=]",
        RegexOptions.IgnoreCase);

    private static readonly Regex EnvironmentAssignment = CreateRegex(
        "^\\s*(?:export\\s+)?[A-Z_][A-Z0-9_]{1,63}\\s*=\\s*(?!\\[REDACTED\\]\\s*$).+$",
        RegexOptions.Multiline);

    private static readonly Regex TerminalPrompt = CreateRegex(
        "^\\s*(?:[$#%>]\\s+|PS\\s+[^>\\r\\n]{0,120}>\\s+|"
        + "[A-Za-z]:\\\\[^>\\r\\n]{0,180}>\\s*)",
        RegexOptions.Multiline);

    private static readonly Regex PrivateKey = CreateRegex(
        "-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----",
        RegexOptions.IgnoreCase);

    private static readonly Regex ProviderToken = CreateRegex(
        "(?:\\bAKIA[0-9A-Z]{16}\\b|\\bgh[pousr]_[A-Za-z0-9]{20,}\\b|"
        + "\\bsk-[A-Za-z0-9_-]{20,}\\b|\\bxox[baprs]-[A-Za-z0-9-]{16,}\\b|"
        + "\\bAIza[0-9A-Za-z_-]{30,}\\b)",
        RegexOptions.IgnoreCase);

    private static readonly Regex JsonWebToken = CreateRegex(
        "\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\b",
        RegexOptions.None);

    private static readonly Regex CredentialUri = CreateRegex(
        "\\b[a-z][a-z0-9+.-]{1,20}://[^/@\\s:]+:[^/@\\s]+@",
        RegexOptions.IgnoreCase);

    public static DiagnosticsSafetyFailure TryNormalizeMetadata(
        string? value,
        out string normalized)
    {
        normalized = string.Empty;
        if (!TryNormalize(value, allowLineBreaks: false, out normalized)
            || string.IsNullOrWhiteSpace(normalized))
        {
            return DiagnosticsSafetyFailure.InvalidText;
        }

        try
        {
            if (!ContainsUnsafeContent(normalized)
                && (!MayContainAssignment(normalized)
                    || !SensitiveAssignment.IsMatch(normalized)))
            {
                return DiagnosticsSafetyFailure.None;
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        normalized = string.Empty;
        return DiagnosticsSafetyFailure.UnsafeContent;
    }

    public static DiagnosticsSafetyFailure TrySanitizeArtifact(
        string? value,
        out string sanitized)
    {
        sanitized = string.Empty;
        if (!TryNormalize(value, allowLineBreaks: true, out var normalized))
        {
            return DiagnosticsSafetyFailure.InvalidText;
        }

        try
        {
            var mayContainAssignment = MayContainAssignment(normalized);
            if (mayContainAssignment && SensitiveStructuredJson.IsMatch(normalized))
            {
                return DiagnosticsSafetyFailure.UnsafeContent;
            }

            var redacted = mayContainAssignment
                ? SensitiveJsonScalar.Replace(
                    normalized,
                    "\"_redacted\":\"[REDACTED]\"")
                : normalized;
            if (mayContainAssignment
                && !TryRedactRemainingAssignments(redacted, out sanitized))
            {
                return DiagnosticsSafetyFailure.UnsafeContent;
            }
            if (!mayContainAssignment)
            {
                sanitized = redacted;
            }

            if (!ContainsUnsafeContent(sanitized)
                && (!MayContainAssignment(sanitized)
                    || !SensitiveAssignment.IsMatch(sanitized)))
            {
                return DiagnosticsSafetyFailure.None;
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        sanitized = string.Empty;
        return DiagnosticsSafetyFailure.UnsafeContent;
    }

    public static bool TryEncodeUtf8(string value, out byte[] bytes)
    {
        try
        {
            bytes = StrictUtf8.GetBytes(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            bytes = [];
            return false;
        }
    }

    public static bool ContainsUnsafePathText(string value)
    {
        try
        {
            return PrivateKey.IsMatch(value)
                || ProviderToken.IsMatch(value)
                || JsonWebToken.IsMatch(value)
                || CredentialUri.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static bool TryNormalize(
        string? value,
        bool allowLineBreaks,
        out string normalized)
    {
        normalized = string.Empty;
        if (value is null)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsControl(character))
            {
                continue;
            }

            var permitted = allowLineBreaks && character is '\r' or '\n' or '\t';
            if (!permitted)
            {
                return false;
            }
        }

        try
        {
            normalized = value
                .Normalize(NormalizationForm.FormC)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            return TryEncodeUtf8(normalized, out _);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryRedactRemainingAssignments(string value, out string redacted)
    {
        var lines = value.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var match = SensitiveAssignment.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var valueStart = match.Index + match.Length;
            if (string.IsNullOrWhiteSpace(lines[index][valueStart..]))
            {
                redacted = string.Empty;
                return false;
            }

            // A non-JSON assignment may contain spaces or shell syntax, so retaining any suffix
            // would risk preserving part of the value. Dropping the remainder is intentionally lossy.
            lines[index] = string.Concat(
                lines[index].AsSpan(0, match.Index),
                "_redacted=",
                RedactionMarker);
        }

        redacted = string.Join('\n', lines);
        return true;
    }

    private static bool ContainsUnsafeContent(string value)
    {
        // The accepted artifact bound is one MiB. Avoid running every timeout-bound regex
        // across a large benign value when none of their literal anchors are present.
        // This is a conservative prefilter: false positives only incur the regex scan.
        if (!MayContainUnsafeContent(value))
        {
            return false;
        }

        return PrivateKey.IsMatch(value)
            || ProviderToken.IsMatch(value)
            || JsonWebToken.IsMatch(value)
            || CredentialUri.IsMatch(value)
            || ForbiddenField.IsMatch(value)
            || EnvironmentAssignment.IsMatch(value)
            || TerminalPrompt.IsMatch(value);
    }

    private static bool MayContainAssignment(string value) =>
        value.Contains(':')
        || value.Contains('=');

    private static bool MayContainUnsafeContent(string value) =>
        MayContainAssignment(value)
        || value.IndexOf("-----BEGIN", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("AKIA", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("ghp_", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("gho_", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("ghu_", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("ghs_", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("ghr_", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("sk-", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("xox", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("AIza", StringComparison.OrdinalIgnoreCase) >= 0
        || value.Contains("eyJ", StringComparison.Ordinal)
        || value.Contains("://", StringComparison.Ordinal)
        || value.Contains('$')
        || value.Contains('#')
        || value.Contains('%')
        || value.Contains('>')
        || value.IndexOf("PS ", StringComparison.OrdinalIgnoreCase) >= 0
        || value.Contains(":\\", StringComparison.Ordinal);

    private static Regex CreateRegex(string pattern, RegexOptions options) =>
        new(
            pattern,
            options | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            RegexTimeout);
}
