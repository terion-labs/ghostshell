using System.Globalization;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// One bounded, run-local clarification requested by untrusted model output.
/// The question is presentation data only and can never grant authority.
/// </summary>
public sealed record GovernedAgentQuestion
{
    public const int MaximumQuestionBytes = 1024;
    public const string UntrustedModelContentOrigin =
        "untrusted_model_question";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public GovernedAgentQuestion(
        AgentQuestionId id,
        string question,
        DateTimeOffset expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException(
                "An agent question requires a correlation identifier.",
                nameof(id));
        }

        ValidateText(
            question,
            MaximumQuestionBytes,
            "Agent question text",
            nameof(question));
        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An agent question expiry must be UTC.",
                nameof(expiresAtUtc));
        }

        Id = id;
        Question = string.Concat(question);
        ExpiresAtUtc = expiresAtUtc;
    }

    public AgentQuestionId Id { get; }

    public string Question { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public string ContentOrigin => UntrustedModelContentOrigin;

    internal static void ValidateText(
        string value,
        int maximumBytes,
        string label,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                $"{label} must contain valid Unicode.",
                parameterName,
                exception);
        }

        if (byteCount > maximumBytes
            || value.EnumerateRunes().Any(rune =>
                Rune.GetUnicodeCategory(rune) is
                    UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator)
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException(
                $"{label} must be single-line, bounded, printable, and non-secret.",
                parameterName);
        }
    }
}
