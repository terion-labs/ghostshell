namespace Asura.Application.Previews;

/// <summary>One thing inside an archive, as the archive's own index records it.</summary>
public sealed record ArchiveEntryDescriptor(
    string Path,
    bool IsDirectory,
    long? Size,
    long? CompressedSize);

/// <summary>
/// Reads what an archive says it contains without extracting any of it. A zip
/// answers from its index alone; a tar has to be walked, but nothing is written
/// anywhere either way.
/// </summary>
public interface IArchiveTableOfContents
{
    bool Claims(string fileName);

    /// <summary>
    /// The entries, or null when the content cannot be read as an archive.
    /// Stops at <paramref name="maximumEntries"/> after skipping <paramref name="offset"/>.
    /// Consumers can request one extra row to detect a following page without
    /// materializing the whole archive. The
    /// file name says which format to expect; the bytes come from the content,
    /// wherever it lives.
    /// </summary>
    ValueTask<IReadOnlyList<ArchiveEntryDescriptor>?> ReadAsync(
        FilePreviewContent content,
        string fileName,
        int maximumEntries,
        CancellationToken cancellationToken,
        int offset = 0);
}

/// <summary>Which names are archives, shared by the previewer and the reader.</summary>
public static class ArchiveFormats
{
    public static bool IsArchive(string fileName) => Kind(fileName) is not ArchiveKind.None;

    public static ArchiveKind Kind(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var name = fileName.ToLowerInvariant();
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal)
            || name.EndsWith(".tgz", StringComparison.Ordinal)
            || name.EndsWith(".tar.bz2", StringComparison.Ordinal)
            || name.EndsWith(".tbz", StringComparison.Ordinal))
        {
            return ArchiveKind.CompressedTar;
        }

        return PreviewText.Extension(fileName) switch
        {
            "zip" or "jar" or "war" or "nupkg" or "vsix" or "whl" or "apk" => ArchiveKind.Zip,
            "tar" => ArchiveKind.Tar,
            _ => ArchiveKind.None,
        };
    }
}

public enum ArchiveKind
{
    None,
    Zip,
    Tar,
    CompressedTar,
}
