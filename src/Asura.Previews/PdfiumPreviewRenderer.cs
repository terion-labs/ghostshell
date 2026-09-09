using System.Runtime.Versioning;
using Asura.Application;
using Asura.Application.Previews;
using PDFtoImage;
using SkiaSharp;

namespace Asura.Previews;

/// <summary>
/// Renders PDF pages with PDFium, the engine browsers use, so a document looks
/// the way its author expects rather than the way a reimplementation guesses.
///
/// PDFium ships native binaries per platform; the three Asura runs on are
/// declared here so the analyzer can hold callers to the same promise.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("macOS")]
[SupportedOSPlatform("linux")]
public sealed class PdfiumPreviewRenderer : IPdfPreviewRenderer
{
    public bool Claims(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && Path.GetExtension(fileName.Trim())
            .Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public ValueTask<int> CountPagesAsync(
        FilePreviewContent content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new ValueTask<int>(Task.Run(
            () =>
            {
                try
                {
                    using var stream = content.OpenRead();
                    return Conversion.GetPageCount(stream, leaveOpen: true);
                }
                catch (Exception exception) when (IsDocumentFailure(exception))
                {
                    // A file that will not open is a preview that cannot be
                    // shown, not a crash.
                    return 0;
                }
            },
            cancellationToken));
    }

    public ValueTask<PdfPageImage?> RenderPageAsync(
        FilePreviewContent content,
        int pageIndex,
        int targetWidth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        return new ValueTask<PdfPageImage?>(Task.Run<PdfPageImage?>(
            () =>
            {
                try
                {
                    using var stream = content.OpenRead();
                    // Left open deliberately: the default closes the stream,
                    // and the page render below needs the same bytes again.
                    var pageCount = Conversion.GetPageCount(stream, leaveOpen: true);
                    if (pageIndex >= pageCount)
                    {
                        return null;
                    }

                    stream.Position = 0;
                    cancellationToken.ThrowIfCancellationRequested();
                    var pageSize = Conversion.GetPageSize(
                        stream,
                        pageIndex,
                        leaveOpen: true,
                        password: null);
                    if (!PreviewRasterBudget.TryFitAspectRatio(
                            pageSize.Width,
                            pageSize.Height,
                            targetWidth,
                            out var renderSize))
                    {
                        return null;
                    }

                    stream.Position = 0;
                    cancellationToken.ThrowIfCancellationRequested();
                    using var bitmap = Conversion.ToImage(
                        stream,
                        leaveOpen: true,
                        password: null,
                        page: pageIndex,
                        options: new RenderOptions(
                            Width: renderSize.Width,
                            Height: renderSize.Height,
                            WithAspectRatio: false));
                    if (bitmap.Width > renderSize.Width
                        || bitmap.Height > renderSize.Height
                        || !PreviewRasterBudget.Contains(
                            bitmap.Width,
                            bitmap.Height))
                    {
                        return null;
                    }

                    using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    return new PdfPageImage(encoded.ToArray(), pageIndex + 1, pageCount);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (IsDocumentFailure(exception))
                {
                    return null;
                }
            },
            cancellationToken));
    }

    /// <summary>
    /// A corrupt, encrypted, or simply not-a-PDF file fails inside PDFium, and
    /// every one of those is an ordinary outcome for a preview. The engine
    /// raises its own exception types, so the net is cast by what must not be
    /// swallowed — cancellation — rather than by listing what may be.
    /// </summary>
    private static bool IsDocumentFailure(Exception exception) =>
        exception is not OperationCanceledException;
}
