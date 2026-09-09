using System.Globalization;
using System.Text;

namespace Asura.Application;

/// <summary>
/// Bounded display-only text derived from an untrusted local process name.
/// Paths, malformed text, unsafe Unicode, and secret-shaped material are
/// replaced before they can cross the governed result boundary.
/// </summary>
public sealed record AgentProcessDisplayName
{
    public const int MaximumTextBytes = 128;
    private const string Redaction = "[REDACTED PROCESS NAME]";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal AgentProcessDisplayName(
        string text,
        bool redacted,
        bool truncated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!IsWellFormedUnicode(text)
            || ContainsUnsafeText(text)
            || StrictUtf8.GetByteCount(text) > MaximumTextBytes)
        {
            throw new ArgumentException(
                "A projected process name must be printable and bounded.",
                nameof(text));
        }

        if (redacted
            && !string.Equals(text, Redaction, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A redacted process name must use the fixed safe replacement.",
                nameof(text));
        }

        Text = string.Concat(text);
        Redacted = redacted;
        Truncated = truncated;
    }

    public string Text { get; }

    public bool Redacted { get; }

    public bool Truncated { get; }

    internal static AgentProcessDisplayName FromUntrusted(string? value)
    {
        var redacted = string.IsNullOrWhiteSpace(value)
            || !IsWellFormedUnicode(value)
            || ContainsUnsafeText(value)
            || LooksPathLike(value)
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value);
        var candidate = redacted ? Redaction : value!;
        var bounded = TruncateUtf8(candidate, MaximumTextBytes);
        return new AgentProcessDisplayName(
            bounded,
            redacted,
            !redacted
            && !string.Equals(candidate, bounded, StringComparison.Ordinal));
    }

    private static bool LooksPathLike(string value) =>
        value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal);

    private static bool ContainsUnsafeText(string value) =>
        value.EnumerateRunes().Any(rune =>
            Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator);

    private static bool IsWellFormedUnicode(string? value)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (StrictUtf8.GetByteCount(value) <= maximumBytes)
        {
            return string.Concat(value);
        }

        var builder = new StringBuilder(value.Length);
        var byteCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            byteCount += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }
}
