using System.Text;
using System.Text.Json;

namespace Asura.Application;

/// <summary>
/// Classifies bounded file content once for every file-panel provider. A Docker
/// file, an SFTP file, and a local file must reach the same preview renderer
/// when their names and bytes are the same.
/// </summary>
public static class FilePanelPreviewClassifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static (FilePanelPreviewKind Kind, string MediaType) Classify(
        FilePanelLocation location,
        ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (IsPng(content))
        {
            return (FilePanelPreviewKind.Image, "image/png");
        }

        if (IsJpeg(content))
        {
            return (FilePanelPreviewKind.Image, "image/jpeg");
        }

        if (IsGif(content))
        {
            return (FilePanelPreviewKind.Image, "image/gif");
        }

        if (IsSqlite(content))
        {
            return (FilePanelPreviewKind.Database, "application/vnd.sqlite3");
        }

        if (content.StartsWith("%PDF-"u8))
        {
            return (FilePanelPreviewKind.Pdf, "application/pdf");
        }

        if (IsTiff(content))
        {
            return (FilePanelPreviewKind.Image, "image/tiff");
        }

        if (IsHeif(content) is { } heifType)
        {
            return (FilePanelPreviewKind.Image, heifType);
        }

        if (IsWebp(content))
        {
            return (FilePanelPreviewKind.Image, "image/webp");
        }

        if (IsBmp(content))
        {
            return (FilePanelPreviewKind.Image, "image/bmp");
        }

        if (IsPsd(content))
        {
            return (FilePanelPreviewKind.Image, "image/vnd.adobe.photoshop");
        }

        if (TryDecodeText(content, out var text))
        {
            if (LooksLikeJson(location, text))
            {
                return (FilePanelPreviewKind.StructuredText, "application/json");
            }

            if (HasExtension(location, ".html") || HasExtension(location, ".htm"))
            {
                return (FilePanelPreviewKind.Html, "text/html; charset=utf-8");
            }

            return (FilePanelPreviewKind.Text, "text/plain; charset=utf-8");
        }

        return (FilePanelPreviewKind.Hex, "application/octet-stream");
    }

    private static bool TryDecodeText(ReadOnlySpan<byte> content, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(content);
            return !text.Contains('\0', StringComparison.Ordinal)
                && text.All(character => !char.IsControl(character)
                    || character is '\r' or '\n' or '\t');
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool HasExtension(FilePanelLocation location, string extension) =>
        location.Address is FilePanelAddress.Hierarchical hierarchical
        && hierarchical.Path.Name?.Value.EndsWith(extension, StringComparison.OrdinalIgnoreCase) == true;

    private static bool LooksLikeJson(FilePanelLocation location, string text)
    {
        var extensionSuggestsJson = HasExtension(location, ".json");
        var trimmed = text.AsSpan().TrimStart();
        if (!extensionSuggestsJson && (trimmed.IsEmpty || trimmed[0] is not ('{' or '[')))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSqlite(ReadOnlySpan<byte> content) =>
        content.StartsWith("SQLite format 3\u0000"u8);

    private static bool IsPng(ReadOnlySpan<byte> content) =>
        content.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    private static bool IsJpeg(ReadOnlySpan<byte> content) =>
        content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF;

    private static bool IsGif(ReadOnlySpan<byte> content) =>
        content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8);

    private static bool IsTiff(ReadOnlySpan<byte> content) =>
        content.StartsWith("II\u002a\u0000"u8) || content.StartsWith("MM\u0000\u002a"u8);

    private static string? IsHeif(ReadOnlySpan<byte> content)
    {
        if (content.Length < 12 || !content[4..].StartsWith("ftyp"u8))
        {
            return null;
        }

        var brand = content.Slice(8, 4);
        if (brand.StartsWith("heic"u8) || brand.StartsWith("heix"u8)
            || brand.StartsWith("hevc"u8) || brand.StartsWith("mif1"u8))
        {
            return "image/heic";
        }

        return brand.StartsWith("avif"u8) || brand.StartsWith("avis"u8)
            ? "image/avif"
            : null;
    }

    private static bool IsWebp(ReadOnlySpan<byte> content) =>
        content.Length >= 12 && content.StartsWith("RIFF"u8) && content[8..].StartsWith("WEBP"u8);

    private static bool IsBmp(ReadOnlySpan<byte> content) => content.StartsWith("BM"u8);

    private static bool IsPsd(ReadOnlySpan<byte> content) => content.StartsWith("8BPS"u8);
}
