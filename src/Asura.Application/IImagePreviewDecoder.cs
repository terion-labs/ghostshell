namespace Asura.Application;

/// <summary>
/// An image decoded into something the presentation layer can draw without
/// knowing the source format: PNG bytes, plus the original's real dimensions so
/// the panel can say what the file actually is rather than what it was scaled
/// to.
/// </summary>
public sealed record DecodedImage(
    ReadOnlyMemory<byte> PngBytes,
    int Width,
    int Height,
    string FormatName);

/// <summary>
/// Decodes image files the drawing stack cannot open on its own — camera and
/// scanner formats, HEIC, TIFF, layered documents.
///
/// It works from whole-file content rather than a bounded head because a head
/// of a JPEG is not a smaller JPEG; where that content lives is the content's
/// business, not the decoder's.
/// </summary>
public interface IImagePreviewDecoder
{
    /// <summary>
    /// Whether this decoder claims the file, judged by name. Claiming is
    /// deliberately conservative: formats the drawing stack already handles
    /// stay with it, so the common case never pays for a conversion.
    /// </summary>
    bool Claims(string fileName);

    /// <summary>
    /// Decodes to PNG, scaled down so the result stays within
    /// <paramref name="maximumPixels"/>. Scaling happens in the decoder because
    /// a 100-megapixel scan must never be materialized as a full-size bitmap
    /// just to be shown in a preview.
    /// </summary>
    ValueTask<DecodedImage?> DecodeAsync(
        FilePreviewContent content,
        long maximumPixels,
        CancellationToken cancellationToken);
}
