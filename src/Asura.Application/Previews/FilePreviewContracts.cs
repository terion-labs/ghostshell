namespace Asura.Application.Previews;

/// <summary>
/// What a previewer is given: the file's name and classification, plus the
/// bounded head the provider already read. Deliberately not the whole file —
/// previewers decide what to show from cheap information, and the few
/// renderings that need every byte say so by naming themselves.
/// </summary>
public sealed record FilePreviewSource(
    string FileName,
    FilePanelPreviewKind Kind,
    string MediaType,
    ReadOnlyMemory<byte> Content,
    bool IsTruncated);

/// <summary>
/// A switch a previewer offers for its format — "Show raw" for a page,
/// "Prettify" for JSON. The panel shows these beside the file's details, and
/// hands the chosen values back on the next call.
/// </summary>
public sealed record FilePreviewToggle(string Id, string Label, bool IsOn);

/// <summary>What to draw, and what the reader may change about it.</summary>
public sealed record FilePreviewOutcome(
    FilePreviewRendering Rendering,
    IReadOnlyList<FilePreviewToggle> Toggles)
{
    public static FilePreviewOutcome For(FilePreviewRendering rendering) =>
        new(rendering, []);
}

/// <summary>
/// One way of showing a file. A closed set: the panel knows how to draw each
/// of these, and formats are added by writing previewers that map onto them
/// rather than by teaching the panel another shape.
/// </summary>
public abstract record FilePreviewRendering;

/// <summary>Text shown as source, optionally syntax-highlighted by name.</summary>
public sealed record SourcePreviewRendering(
    string Text,
    string SyntaxFileName,
    bool Wrap = true) : FilePreviewRendering;

/// <summary>Text laid out as Markdown.</summary>
public sealed record MarkdownPreviewRendering(string Text) : FilePreviewRendering;

/// <summary>Rows and columns — a delimited file, shown as what it is.</summary>
public sealed record TablePreviewRendering(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string Summary) : FilePreviewRendering;

/// <summary>
/// The contents of an archive, listed rather than unpacked. Needs the whole
/// file: an archive's index is not in its first bytes.
/// </summary>
public sealed record ArchivePreviewRendering : FilePreviewRendering;

/// <summary>A picture, decoded from the whole file.</summary>
public sealed record ImagePreviewRendering : FilePreviewRendering;

/// <summary>A document, rendered a page at a time.</summary>
public sealed record PdfPreviewRendering : FilePreviewRendering;

/// <summary>A web page, rendered by the embedded browser.</summary>
public sealed record WebPagePreviewRendering : FilePreviewRendering;

/// <summary>A database, opened with the shell's database viewer.</summary>
public sealed record DatabasePreviewRendering : FilePreviewRendering;

/// <summary>
/// One format's reading of a file. Previewers are pure: name, bytes and
/// switches in, a rendering out. Anything needing the file on disk names a
/// rendering that asks for it, so no previewer performs IO of its own.
/// </summary>
public interface IFilePreviewer
{
    /// <summary>Whether this previewer is the one for the file.</summary>
    bool Claims(FilePreviewSource source);

    /// <summary>
    /// The rendering for the file, given the switches currently chosen. A
    /// switch absent from <paramref name="toggles"/> takes the previewer's
    /// own default.
    /// </summary>
    FilePreviewOutcome Create(
        FilePreviewSource source,
        IReadOnlyDictionary<string, bool> toggles);
}
