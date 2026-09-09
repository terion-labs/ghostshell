using System.Text;

namespace Asura.Application.Previews;

/// <summary>
/// Markdown, laid out as prose by default and available as its source. The
/// judgement is by name: a Markdown file is Markdown whether or not it happens
/// to contain any markup.
/// </summary>
public sealed class MarkdownPreviewer : IFilePreviewer
{
    public const string RawToggle = "raw";

    public bool Claims(FilePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return PreviewText.Extension(source.FileName)
            is "md" or "markdown" or "mdown" or "mkd";
    }

    public FilePreviewOutcome Create(
        FilePreviewSource source,
        IReadOnlyDictionary<string, bool> toggles)
    {
        ArgumentNullException.ThrowIfNull(source);
        var raw = PreviewText.IsOn(toggles, RawToggle, byDefault: false);
        var text = PreviewText.Utf8(source.Content.Span, source.IsTruncated);
        return new FilePreviewOutcome(
            raw
                ? new SourcePreviewRendering(text, source.FileName)
                : new MarkdownPreviewRendering(text),
            [new FilePreviewToggle(RawToggle, "Show raw", raw)]);
    }
}

/// <summary>
/// A web page, shown as inert markup by default. Live rendering is an explicit
/// opt-in because HTML can run scripts and request network resources.
/// </summary>
public sealed class WebPagePreviewer : IFilePreviewer
{
    public const string RawToggle = "raw";

    public bool Claims(FilePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind == FilePanelPreviewKind.Html
            || PreviewText.Extension(source.FileName) is "html" or "htm" or "xhtml";
    }

    public FilePreviewOutcome Create(
        FilePreviewSource source,
        IReadOnlyDictionary<string, bool> toggles)
    {
        ArgumentNullException.ThrowIfNull(source);
        var raw = PreviewText.IsOn(toggles, RawToggle, byDefault: true);
        return new FilePreviewOutcome(
            raw
                ? new SourcePreviewRendering(
                    PreviewText.Utf8(source.Content.Span, source.IsTruncated),
                    source.FileName)
                : new WebPagePreviewRendering(),
            [new FilePreviewToggle(RawToggle, "Show source", raw)]);
    }
}

/// <summary>
/// JSON and XML, indented by default. Turning it off shows the file as it is
/// written, which is the only way to see how it is actually laid out.
/// </summary>
public sealed class StructuredDataPreviewer : IFilePreviewer
{
    public const string PrettifyToggle = "prettify";

    private static readonly string[] JsonExtensions = ["json", "jsonc", "webmanifest"];

    private static readonly string[] XmlExtensions =
        ["xml", "xsd", "xsl", "xslt", "svg", "plist", "csproj", "props", "targets", "resx"];

    public bool Claims(FilePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var extension = PreviewText.Extension(source.FileName);
        return JsonExtensions.Contains(extension)
            || XmlExtensions.Contains(extension)
            || source.MediaType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
    }

    public FilePreviewOutcome Create(
        FilePreviewSource source,
        IReadOnlyDictionary<string, bool> toggles)
    {
        ArgumentNullException.ThrowIfNull(source);
        var prettify = PreviewText.IsOn(toggles, PrettifyToggle, byDefault: true);
        var isXml = XmlExtensions.Contains(PreviewText.Extension(source.FileName));
        var text = prettify
            ? isXml
                ? PreviewText.Xml(source.Content.Span, source.IsTruncated)
                : PreviewText.Json(source.Content.Span, source.IsTruncated)
            : PreviewText.Utf8(source.Content.Span, source.IsTruncated);
        return new FilePreviewOutcome(
            new SourcePreviewRendering(text, source.FileName),
            [new FilePreviewToggle(PrettifyToggle, "Prettify", prettify)]);
    }
}

/// <summary>
/// Comma- and tab-separated files, shown as the table they describe. The raw
/// text stays one switch away, because a malformed row is easier to find in
/// the source than in a grid that quietly swallowed it.
/// </summary>
public sealed class DelimitedTextPreviewer : IFilePreviewer
{
    public const string TableToggle = "table";

    /// <summary>
    /// Rows laid out at once. A preview is a look at a file, not a spreadsheet;
    /// past this the table says how much more there is.
    /// </summary>
    public const int MaximumRows = 500;

    public bool Claims(FilePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return PreviewText.Extension(source.FileName) is "csv" or "tsv";
    }

    public FilePreviewOutcome Create(
        FilePreviewSource source,
        IReadOnlyDictionary<string, bool> toggles)
    {
        ArgumentNullException.ThrowIfNull(source);
        var asTable = PreviewText.IsOn(toggles, TableToggle, byDefault: true);
        var text = PreviewText.Utf8(source.Content.Span, source.IsTruncated);
        var toggle = new FilePreviewToggle(TableToggle, "As table", asTable);
        if (!asTable)
        {
            return new FilePreviewOutcome(
                new SourcePreviewRendering(text, source.FileName),
                [toggle]);
        }

        var separator = string.Equals(PreviewText.Extension(source.FileName), "tsv", StringComparison.Ordinal) ? '\t' : ',';
        var rows = DelimitedText.Parse(
            Encoding.UTF8.GetString(source.Content.Span),
            separator,
            MaximumRows + 1);
        if (rows.Count == 0)
        {
            return new FilePreviewOutcome(
                new SourcePreviewRendering(text, source.FileName),
                [toggle]);
        }

        // The last row of a bounded read is usually half a line; showing it as
        // data would invent values the file does not contain.
        var body = rows.Skip(1).ToList();
        if (source.IsTruncated && body.Count > 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        var shown = body.Take(MaximumRows).ToArray();
        var summary = Summarize(shown.Length, body.Count, source.IsTruncated);
        return new FilePreviewOutcome(
            new TablePreviewRendering(rows[0], shown, summary),
            [toggle]);
    }

    private static string Summarize(int shown, int available, bool truncated)
    {
        var rows = shown == 1 ? "1 row" : $"{shown} rows";
        if (available > shown)
        {
            return $"{rows} of {available} read";
        }

        return truncated ? $"{rows} — more follow in the file" : rows;
    }
}

/// <summary>
/// An archive, listed rather than unpacked. Claimed by name: the listing needs
/// the whole file, and asking for it is the rendering's business.
/// </summary>
public sealed class ArchivePreviewer : IFilePreviewer
{
    public bool Claims(FilePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ArchiveFormats.IsArchive(source.FileName);
    }

    public FilePreviewOutcome Create(
        FilePreviewSource source,
        IReadOnlyDictionary<string, bool> toggles)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = toggles;
        return FilePreviewOutcome.For(new ArchivePreviewRendering());
    }
}

/// <summary>
/// The reading the provider's own classification implies, for everything no
/// format claimed: pictures, documents, databases, plain text, and bytes.
/// </summary>
public sealed class ClassifiedFilePreviewer : IFilePreviewer
{
    public bool Claims(FilePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return true;
    }

    public FilePreviewOutcome Create(
        FilePreviewSource source,
        IReadOnlyDictionary<string, bool> toggles)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = toggles;
        return FilePreviewOutcome.For(source.Kind switch
        {
            FilePanelPreviewKind.Image => new ImagePreviewRendering(),
            FilePanelPreviewKind.Pdf => new PdfPreviewRendering(),
            FilePanelPreviewKind.Database => new DatabasePreviewRendering(),
            FilePanelPreviewKind.Html => new WebPagePreviewRendering(),
            FilePanelPreviewKind.StructuredText => new SourcePreviewRendering(
                PreviewText.Json(source.Content.Span, source.IsTruncated),
                source.FileName),
            FilePanelPreviewKind.Hex => new SourcePreviewRendering(
                PreviewText.Hex(source.Content.Span, source.IsTruncated),
                source.FileName,
                Wrap: false),
            _ => new SourcePreviewRendering(
                PreviewText.Utf8(source.Content.Span, source.IsTruncated),
                source.FileName),
        });
    }
}
