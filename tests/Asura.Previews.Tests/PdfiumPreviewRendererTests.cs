using System.Runtime.Versioning;
using Asura.Previews;

namespace Asura.Previews.Tests;

/// <summary>
/// The renderer declares the desktop platforms PDFium ships binaries for, and
/// the suite runs on those, so the tests carry the same declaration.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("macOS")]
[SupportedOSPlatform("linux")]
public sealed class PdfiumPreviewRendererTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("asura-pdf-preview").FullName;

    private readonly PdfiumPreviewRenderer _renderer = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("REPORT.PDF")]
    public void Pdf_files_are_claimed(string fileName) =>
        Assert.True(_renderer.Claims(fileName));

    [Theory]
    [InlineData("notes.md")]
    [InlineData("scan.tiff")]
    [InlineData("")]
    public void Other_files_are_not(string fileName) =>
        Assert.False(_renderer.Claims(fileName));

    [Fact]
    public async Task Every_page_of_a_document_is_counted()
    {
        var path = await WriteProbeAsync("two-pages.pdf");

        Assert.Equal(2, await _renderer.CountPagesAsync(Content(path), CancellationToken.None));
    }

    [Fact]
    public async Task A_page_renders_to_png_at_the_requested_width()
    {
        var path = await WriteProbeAsync("render.pdf");

        var page = await _renderer.RenderPageAsync(Content(path), 0, 600, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(1, page!.PageNumber);
        Assert.Equal(2, page.PageCount);
        Assert.True(page.PngBytes.Span.StartsWith(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.Equal(600, PngWidth(page.PngBytes.Span));
    }

    [Fact]
    public async Task A_page_keeps_its_shape()
    {
        var path = await WriteProbeAsync("shape.pdf");

        var page = await _renderer.RenderPageAsync(Content(path), 0, 600, CancellationToken.None);

        Assert.NotNull(page);
        // The probe's pages are US Letter, 612x792 points. Rendering to a width
        // alone must not stretch the page to some default height.
        var (width, height) = PngSize(page!.PngBytes.Span);
        Assert.Equal(600, width);
        Assert.InRange(height, (int)(600 * 792d / 612 * 0.98), (int)(600 * 792d / 612 * 1.02));
    }

    [Fact]
    public async Task An_extreme_page_is_rejected_before_output_raster_allocation()
    {
        var path = Path.Combine(_root, "extreme-page.pdf");
        await File.WriteAllTextAsync(path, TestDocuments.TwoPagePdf(1, 100_000));

        Assert.Null(await _renderer.RenderPageAsync(
            Content(path),
            0,
            1_600,
            CancellationToken.None));
    }

    [Fact]
    public async Task Paging_past_the_end_renders_nothing_rather_than_wrapping()
    {
        var path = await WriteProbeAsync("bounds.pdf");

        Assert.NotNull(await _renderer.RenderPageAsync(Content(path), 1, 400, CancellationToken.None));
        Assert.Null(await _renderer.RenderPageAsync(Content(path), 2, 400, CancellationToken.None));
    }

    [Fact]
    public async Task A_file_that_is_not_a_pdf_counts_zero_pages_rather_than_throwing()
    {
        var path = Path.Combine(_root, "broken.pdf");
        await File.WriteAllTextAsync(path, "this is not a PDF");

        Assert.Equal(0, await _renderer.CountPagesAsync(Content(path), CancellationToken.None));
        Assert.Null(await _renderer.RenderPageAsync(Content(path), 0, 400, CancellationToken.None));
    }

    private async Task<string> WriteProbeAsync(string name)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, TestDocuments.TwoPagePdf());
        return path;
    }

    private static int PngWidth(ReadOnlySpan<byte> png) => PngSize(png).Width;

    private static (int Width, int Height) PngSize(ReadOnlySpan<byte> png) =>
        (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[16..]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[20..]));

    private static Asura.Application.FilePreviewContent Content(string path) =>
        Asura.Application.FilePreviewContent.FromLocalFile(path);
}
