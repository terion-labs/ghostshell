using System.Globalization;
using System.Text;

namespace Asura.Application;

/// <summary>
/// One bounded, presentation-safe progress update from untrusted model output.
/// The fixed content origin prevents model text from claiming trusted provenance.
/// </summary>
public sealed record GovernedAgentProgress
{
    public const int MaximumMessageBytes = 512;
    public const string UntrustedModelContentOrigin = "untrusted_model_progress";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public GovernedAgentProgress(string message, int? percent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (GetStrictUtf8ByteCount(message, nameof(message)) > MaximumMessageBytes
            || message.EnumerateRunes().Any(rune =>
                Rune.GetUnicodeCategory(rune) is
                    UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator)
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(message))
        {
            throw new ArgumentException(
                "Agent progress must be single-line, bounded, printable, and non-secret.",
                nameof(message));
        }

        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percent),
                "Agent progress percent must be between 0 and 100.");
        }

        Message = string.Concat(message);
        Percent = percent;
    }

    public string Message { get; }

    public int? Percent { get; }

    /// <summary>
    /// Fixed provenance label; it can never be supplied or changed by the model.
    /// </summary>
    public string ContentOrigin => UntrustedModelContentOrigin;

    private static int GetStrictUtf8ByteCount(
        string value,
        string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Agent progress must contain valid Unicode text.",
                parameterName,
                exception);
        }
    }
}
