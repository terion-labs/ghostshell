using System.Globalization;
using System.Text;

namespace Asura.Application;

/// <summary>
/// Non-secret presentation metadata produced by a trusted connection adapter.
/// It describes where a terminal crosses a connection boundary without exposing
/// executable arguments, environment values, or credential material.
/// </summary>
public sealed record TerminalConnectionMetadata
{
    public const int MaximumBoundaryBytes = 512;
    public const int MaximumConnectionIdBytes = 256;
    public const int MaximumWorkingDirectoryBytes = 2 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public TerminalConnectionMetadata(
        string connectionBoundary,
        string? initialWorkingDirectory)
    {
        ConnectionBoundary = CopyPrintable(
            connectionBoundary,
            MaximumBoundaryBytes,
            nameof(connectionBoundary),
            required: true)!;
        InitialWorkingDirectory = CopyPrintable(
            initialWorkingDirectory,
            MaximumWorkingDirectoryBytes,
            nameof(initialWorkingDirectory),
            required: false);
    }

    public string ConnectionBoundary { get; }

    public string? InitialWorkingDirectory { get; }

    internal static string? CopyWorkingDirectory(
        string? workingDirectory,
        string parameterName) =>
        CopyPrintable(
            workingDirectory,
            MaximumWorkingDirectoryBytes,
            parameterName,
            required: false);

    internal static void ValidateConnectionId(
        Asura.Core.ConnectionId? connectionId,
        string parameterName)
    {
        if (connectionId is not { } id)
        {
            return;
        }

        var value = id.Value;
        if (value.Any(character =>
                char.IsControl(character)
                || char.GetUnicodeCategory(character) is
                    UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator)
            || GetByteCount(value, parameterName) > MaximumConnectionIdBytes)
        {
            throw new ArgumentException(
                "A terminal connection identity must be printable and bounded.",
                parameterName);
        }
    }

    private static string? CopyPrintable(
        string? value,
        int maximumBytes,
        string parameterName,
        bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                throw new ArgumentException(
                    "Terminal connection metadata requires a human-readable boundary.",
                    parameterName);
            }

            return null;
        }

        _ = GetByteCount(value, parameterName);
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\0':
                    builder.Append(@"\0");
                    break;
                case '\a':
                    builder.Append(@"\a");
                    break;
                case '\b':
                    builder.Append(@"\b");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\v':
                    builder.Append(@"\v");
                    break;
                case '\f':
                    builder.Append(@"\f");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                default:
                    var category = Rune.GetUnicodeCategory(rune);
                    if (category is UnicodeCategory.Control
                        or UnicodeCategory.Format
                        or UnicodeCategory.LineSeparator
                        or UnicodeCategory.ParagraphSeparator)
                    {
                        builder
                            .Append(@"\u{")
                            .Append(rune.Value.ToString("X", CultureInfo.InvariantCulture))
                            .Append('}');
                    }
                    else
                    {
                        builder.Append(rune);
                    }

                    break;
            }
        }

        var copy = builder.ToString();
        if (GetByteCount(copy, parameterName) > maximumBytes)
        {
            throw new ArgumentException(
                $"Terminal connection metadata must not exceed {maximumBytes} UTF-8 bytes.",
                parameterName);
        }

        return copy;
    }

    private static int GetByteCount(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Terminal connection metadata must contain valid Unicode text.",
                parameterName,
                exception);
        }
    }
}
