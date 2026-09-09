namespace Asura.Application.Previews;

/// <summary>
/// The previewers in the order they are asked. The first to claim a file
/// decides how it is shown; the last entry claims everything, so a file always
/// has a reading even if it is only its bytes.
/// </summary>
public sealed class FilePreviewCatalog
{
    private readonly IReadOnlyList<IFilePreviewer> _previewers;

    public FilePreviewCatalog(IEnumerable<IFilePreviewer>? previewers = null)
    {
        _previewers = previewers?.ToArray() ?? Default;
    }

    /// <summary>
    /// The shipped set. Format-specific readings come first, the classification
    /// the provider made comes last: a name that says "csv" beats a media type
    /// that only says "text".
    /// </summary>
    public static IReadOnlyList<IFilePreviewer> Default { get; } =
    [
        new ArchivePreviewer(),
        new DelimitedTextPreviewer(),
        new MarkdownPreviewer(),
        new WebPagePreviewer(),
        new StructuredDataPreviewer(),
        new BinaryPreviewer(),
        new ClassifiedFilePreviewer(),
    ];

    public FilePreviewOutcome Create(
        FilePreviewSource source,
        IReadOnlyDictionary<string, bool>? toggles = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var chosen = toggles ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var previewer in _previewers)
        {
            if (previewer.Claims(source))
            {
                return previewer.Create(source, chosen);
            }
        }

        return FilePreviewOutcome.For(
            new SourcePreviewRendering(
                PreviewText.Hex(source.Content.Span, source.IsTruncated),
                source.FileName,
                Wrap: false));
    }
}
