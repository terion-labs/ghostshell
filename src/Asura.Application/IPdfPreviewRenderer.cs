namespace Asura.Application;

/// <summary>
/// One rendered page, with the document's extent so the presentation can page
/// through it without asking again.
/// </summary>
public sealed record PdfPageImage(
    ReadOnlyMemory<byte> PngBytes,
    int PageNumber,
    int PageCount);

/// <summary>
/// Renders pages of a PDF to images.
///
/// A page at a time, from whole-file content: only the page being looked at is
/// rasterized, so a thousand-page file costs the same as a one-page file until
/// the user pages into it. Each call opens the content afresh — the renderer
/// holds nothing between pages.
/// </summary>
public interface IPdfPreviewRenderer
{
    bool Claims(string fileName);

    /// <summary>How many pages the document has, or zero when it cannot be read.</summary>
    ValueTask<int> CountPagesAsync(
        FilePreviewContent content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Rasterizes one zero-based page to PNG at roughly
    /// <paramref name="targetWidth"/> pixels wide, or null when the page cannot
    /// be rendered.
    /// </summary>
    ValueTask<PdfPageImage?> RenderPageAsync(
        FilePreviewContent content,
        int pageIndex,
        int targetWidth,
        CancellationToken cancellationToken);
}
