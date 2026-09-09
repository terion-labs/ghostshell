using System.Text;
using Asura.Application.Previews;

namespace Asura.Application.Tests;

/// <summary>
/// Which previewer claims a file, and what each one does with the switches it
/// offers. The panel draws renderings, so these are the whole of the decision
/// about how a format is shown.
/// </summary>
public sealed class FilePreviewCatalogTests
{
    private static readonly FilePreviewCatalog Catalog = new();

    [Theory]
    [InlineData("notes.md", typeof(MarkdownPreviewRendering))]
    [InlineData("NOTES.MARKDOWN", typeof(MarkdownPreviewRendering))]
    [InlineData("data.csv", typeof(TablePreviewRendering))]
    [InlineData("data.tsv", typeof(TablePreviewRendering))]
    [InlineData("bundle.zip", typeof(ArchivePreviewRendering))]
    [InlineData("bundle.tar.gz", typeof(ArchivePreviewRendering))]
    [InlineData("settings.json", typeof(SourcePreviewRendering))]
    [InlineData("page.html", typeof(SourcePreviewRendering))]
    public void A_file_is_read_by_the_previewer_its_name_names(string name, Type rendering)
    {
        var outcome = Catalog.Create(Source(name, "a,b\n1,2\n"));

        Assert.IsType(rendering, outcome.Rendering);
    }

    [Fact]
    public void A_binary_file_is_named_rather_than_dumped()
    {
        var outcome = Catalog.Create(Binary("payload.bin"));

        // A wall of hex tells almost nobody anything; the format and a symbol
        // for it do, and the bytes are one switch away.
        var binary = Assert.IsType<BinaryPreviewRendering>(outcome.Rendering);
        Assert.Equal("BIN binary", binary.FormatName);
        Assert.Equal("Show hex", Assert.Single(outcome.Toggles).Label);
    }

    [Fact]
    public void The_bytes_come_as_rows_rather_than_one_long_string()
    {
        var outcome = Catalog.Create(
            Binary("payload.bin"),
            new Dictionary<string, bool>(StringComparer.Ordinal) { [BinaryPreviewer.HexToggle] = true });

        // Rows, because a dump handed to a text view costs a measure of every
        // line before anything can be drawn; a list draws what is on screen.
        var hex = Assert.IsType<HexPreviewRendering>(outcome.Rendering);
        var row = Assert.Single(hex.Rows);
        Assert.Equal("00000000", row.Offset);
        Assert.StartsWith("01 02 03", row.Bytes, StringComparison.Ordinal);
        Assert.Equal("...", row.Characters);
    }

    [Fact]
    public void A_dump_says_when_it_is_only_the_start_of_the_file()
    {
        var content = new byte[PreviewText.MaximumHexBytes + 4_096];
        var outcome = Catalog.Create(
            new FilePreviewSource(
                "large.bin",
                FilePanelPreviewKind.Hex,
                "application/octet-stream",
                content,
                IsTruncated: false),
            new Dictionary<string, bool>(StringComparer.Ordinal) { [BinaryPreviewer.HexToggle] = true });

        var hex = Assert.IsType<HexPreviewRendering>(outcome.Rendering);
        Assert.Equal(PreviewText.MaximumHexBytes / 16, hex.Rows.Count);
        Assert.StartsWith("First ", hex.Summary, StringComparison.Ordinal);
    }

    private static FilePreviewSource Binary(string name) =>
        new(
            name,
            FilePanelPreviewKind.Hex,
            "application/octet-stream",
            new byte[] { 1, 2, 3 },
            IsTruncated: false);

    [Fact]
    public void Ordinary_text_wraps()
    {
        var outcome = Catalog.Create(Source("readme.txt", "hello"));

        Assert.True(Assert.IsType<SourcePreviewRendering>(outcome.Rendering).Wrap);
    }

    [Fact]
    public void Markdown_can_be_shown_as_its_source()
    {
        var raw = Catalog.Create(
            Source("notes.md", "# Title"),
            new Dictionary<string, bool>(StringComparer.Ordinal) { [MarkdownPreviewer.RawToggle] = true });

        Assert.IsType<SourcePreviewRendering>(raw.Rendering);
        var toggle = Assert.Single(raw.Toggles);
        Assert.Equal("Show raw", toggle.Label);
        Assert.True(toggle.IsOn);
    }

    [Fact]
    public void A_web_page_is_inert_by_default_and_live_rendering_is_explicit()
    {
        var source = Source("page.html", "<h1>hi</h1>");

        Assert.IsType<SourcePreviewRendering>(Catalog.Create(source).Rendering);
        Assert.IsType<WebPagePreviewRendering>(Catalog.Create(
            source,
            new Dictionary<string, bool>(StringComparer.Ordinal) { [WebPagePreviewer.RawToggle] = false }).Rendering);
        var toggle = Assert.Single(Catalog.Create(source).Toggles);
        Assert.Equal("Show source", toggle.Label);
        Assert.True(toggle.IsOn);
    }

    [Fact]
    public void Json_is_indented_by_default_and_left_alone_when_asked()
    {
        var source = Source("settings.json", """{"a":1,"b":[2,3]}""");

        var prettified = Assert.IsType<SourcePreviewRendering>(
            Catalog.Create(source).Rendering);
        Assert.Contains("\n", prettified.Text, StringComparison.Ordinal);

        var raw = Assert.IsType<SourcePreviewRendering>(Catalog.Create(
            source,
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [StructuredDataPreviewer.PrettifyToggle] = false,
            }).Rendering);
        Assert.Equal("""{"a":1,"b":[2,3]}""", raw.Text);
    }

    [Fact]
    public void Xml_is_indented_too()
    {
        var outcome = Catalog.Create(Source("app.xml", "<a><b>1</b></a>"));

        var rendering = Assert.IsType<SourcePreviewRendering>(outcome.Rendering);
        Assert.Contains("\n  <b>", rendering.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_structured_data_is_shown_as_written_rather_than_as_an_error()
    {
        var outcome = Catalog.Create(Source("broken.json", "{not json"));

        Assert.Equal(
            "{not json",
            Assert.IsType<SourcePreviewRendering>(outcome.Rendering).Text);
    }

    [Fact]
    public void A_truncated_file_is_not_reformatted()
    {
        // Half a document cannot be parsed; showing it as it arrived is honest,
        // and the notice says why it stops.
        var outcome = Catalog.Create(
            new FilePreviewSource(
                "settings.json",
                FilePanelPreviewKind.StructuredText,
                "application/json",
                Encoding.UTF8.GetBytes("""{"a":1,"""),
                IsTruncated: true));

        Assert.Contains(
            PreviewText.TruncationNotice,
            Assert.IsType<SourcePreviewRendering>(outcome.Rendering).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_delimited_file_becomes_its_header_and_rows()
    {
        var outcome = Catalog.Create(Source("people.csv", "name,city\nada,london\ngrace,york\n"));

        var table = Assert.IsType<TablePreviewRendering>(outcome.Rendering);
        Assert.Equal(["name", "city"], table.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["grace", "york"], table.Rows[1]);
        Assert.Equal("2 rows", table.Summary);
    }

    [Fact]
    public void A_delimited_file_can_be_read_as_text_instead()
    {
        var outcome = Catalog.Create(
            Source("people.csv", "name,city\nada,london\n"),
            new Dictionary<string, bool>(StringComparer.Ordinal) { [DelimitedTextPreviewer.TableToggle] = false });

        Assert.IsType<SourcePreviewRendering>(outcome.Rendering);
    }

    [Fact]
    public void The_half_row_at_the_end_of_a_bounded_read_is_not_shown_as_data()
    {
        var outcome = Catalog.Create(
            new FilePreviewSource(
                "people.csv",
                FilePanelPreviewKind.Text,
                "text/plain",
                Encoding.UTF8.GetBytes("name,city\nada,london\ngra"),
                IsTruncated: true));

        var table = Assert.IsType<TablePreviewRendering>(outcome.Rendering);
        var row = Assert.Single(table.Rows);
        Assert.Equal(["ada", "london"], row);
        Assert.Contains("more follow", table.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_delimited_file_falls_back_to_text()
    {
        Assert.IsType<SourcePreviewRendering>(
            Catalog.Create(Source("empty.csv", string.Empty)).Rendering);
    }

    [Fact]
    public void The_classification_decides_when_no_format_claims_the_file()
    {
        foreach (var (kind, rendering) in new (FilePanelPreviewKind, Type)[]
                 {
                     (FilePanelPreviewKind.Image, typeof(ImagePreviewRendering)),
                     (FilePanelPreviewKind.Pdf, typeof(PdfPreviewRendering)),
                     (FilePanelPreviewKind.Database, typeof(DatabasePreviewRendering)),
                 })
        {
            var outcome = Catalog.Create(
                new FilePreviewSource("file.bin", kind, "application/octet-stream", default, false));
            Assert.IsType(rendering, outcome.Rendering);
        }
    }

    private static FilePreviewSource Source(string name, string content) =>
        new(
            name,
            FilePanelPreviewKind.Text,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(content),
            IsTruncated: false);
}
