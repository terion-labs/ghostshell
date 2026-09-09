using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Requests replacement of one editable element's value in an exact browser
/// document revision.
/// </summary>
public sealed record BrowserElementFillRequest
{
    public const int MaximumTextBytes = 2_048;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public BrowserElementFillRequest(
        SessionId sessionId,
        BrowserElementReferenceId reference,
        long documentRevision,
        string text)
    {
        if (string.IsNullOrEmpty(sessionId.Value))
        {
            throw new ArgumentException(
                "A browser element fill requires a valid session ID.",
                nameof(sessionId));
        }

        if (string.IsNullOrEmpty(reference.Value))
        {
            throw new ArgumentException(
                "A browser element fill requires a valid reference ID.",
                nameof(reference));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(documentRevision);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Any(character =>
                char.IsControl(character)
                && character is not '\t' and not '\n' and not '\r'))
        {
            throw new ArgumentException(
                "Browser fill text may contain tabs and line breaks but no other control characters.",
                nameof(text));
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Browser fill text must contain well-formed Unicode.",
                nameof(text),
                exception);
        }

        if (byteCount > MaximumTextBytes)
        {
            throw new ArgumentException(
                $"Browser fill text cannot exceed {MaximumTextBytes} UTF-8 bytes.",
                nameof(text));
        }

        SessionId = sessionId;
        Reference = reference;
        DocumentRevision = documentRevision;
        Text = string.Concat(text);
    }

    public SessionId SessionId { get; }

    public BrowserElementReferenceId Reference { get; }

    public long DocumentRevision { get; }

    public string Text { get; }

    public override string ToString() =>
        $"Browser element fill ({StrictUtf8.GetByteCount(Text)} UTF-8 bytes)";
}
