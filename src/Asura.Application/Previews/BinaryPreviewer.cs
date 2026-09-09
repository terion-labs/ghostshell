namespace Asura.Application.Previews;

/// <summary>
/// What is known about a file whose bytes say nothing a reader can use: what
/// kind of thing it is, and a symbol standing for it. The hex dump is one
/// switch away for when the bytes are the point.
/// </summary>
public sealed record BinaryPreviewRendering(
    string Symbol,
    string FormatName,
    string Detail) : FilePreviewRendering;

/// <summary>
/// One line of a hex dump, already split into its columns. Rows rather than
/// one long string because a dump is a grid of tens of thousands of identical
/// lines: handed to a text editor it costs a full measure of every line before
/// anything can be drawn, and handed to a list only what is on screen is.
/// </summary>
public sealed record HexPreviewRow(string Offset, string Bytes, string Characters);

/// <summary>A file shown as its bytes.</summary>
public sealed record HexPreviewRendering(
    IReadOnlyList<HexPreviewRow> Rows,
    string Summary) : FilePreviewRendering;

/// <summary>
/// The reading for a binary file. A wall of hex tells almost nobody anything
/// about a font or a video; naming the format and showing its symbol does, and
/// the dump is still there for whoever wants it.
/// </summary>
public sealed class BinaryPreviewer : IFilePreviewer
{
    public const string HexToggle = "hex";

    public bool Claims(FilePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind == FilePanelPreviewKind.Hex;
    }

    public FilePreviewOutcome Create(
        FilePreviewSource source,
        IReadOnlyDictionary<string, bool> toggles)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(toggles);
        var hex = PreviewText.IsOn(toggles, HexToggle, byDefault: false);
        var toggle = new FilePreviewToggle(HexToggle, "Show hex", hex);
        if (hex)
        {
            return new FilePreviewOutcome(
                PreviewText.HexRows(source.Content.Span, source.IsTruncated),
                [toggle]);
        }

        var format = BinaryFormats.Describe(source.FileName, source.MediaType);
        return new FilePreviewOutcome(
            new BinaryPreviewRendering(format.Symbol, format.Name, format.Detail),
            [toggle]);
    }
}

/// <summary>
/// Names and symbols for the binary formats worth recognising. The symbol is a
/// name rather than an icon: which glyph draws it is the panel's business, not
/// this layer's.
/// </summary>
public static class BinaryFormats
{
    public sealed record BinaryFormat(string Symbol, string Name, string Detail);

    public static BinaryFormat Describe(string fileName, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var extension = PreviewText.Extension(fileName);
        return extension switch
        {
            "mp3" or "wav" or "flac" or "aac" or "ogg" or "m4a" or "opus" or "aiff" =>
                new BinaryFormat("MusicNote1", Spell(extension) + " audio", "Audio file"),
            "mp4" or "mov" or "mkv" or "avi" or "webm" or "m4v" or "wmv" =>
                new BinaryFormat("Video", Spell(extension) + " video", "Video file"),
            "ttf" or "otf" or "woff" or "woff2" or "eot" =>
                new BinaryFormat("TextFont", Spell(extension) + " font", "Typeface"),
            "exe" or "dll" or "so" or "dylib" or "bin" or "o" or "a" or "wasm" =>
                new BinaryFormat("Code", Spell(extension) + " binary", "Compiled code"),
            "iso" or "img" or "dmg" or "vhd" or "vmdk" =>
                new BinaryFormat("Box", Spell(extension) + " image", "Disk image"),
            "pack" or "idx" or "db" or "sqlite" or "dat" or "bin_" =>
                new BinaryFormat("Database", Spell(extension) + " data", "Data file"),
            "psd" or "ai" or "sketch" or "fig" or "xcf" =>
                new BinaryFormat("Image", Spell(extension) + " document", "Design document"),
            "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "odt" or "ods" =>
                new BinaryFormat("DocumentBulletList", Spell(extension) + " document", "Office document"),
            "" => new BinaryFormat("Document", "Binary file", DescribeMedia(mediaType)),
            _ => new BinaryFormat(
                "Document",
                Spell(extension) + " file",
                DescribeMedia(mediaType)),
        };
    }

    private static string DescribeMedia(string mediaType) =>
        string.IsNullOrWhiteSpace(mediaType)
            || mediaType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? "No preview for this format"
            : mediaType;

    /// <summary>
    /// An extension as a person writes it: "PDF", not "pdf" — but "WebM"
    /// rather than "WEBM" would need a table, and the upper case reads fine.
    /// </summary>
    private static string Spell(string extension) => extension.ToUpperInvariant();
}
