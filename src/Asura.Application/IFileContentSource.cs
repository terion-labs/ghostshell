namespace Asura.Application;

/// <summary>
/// A file's whole content, readable without saying where it lives. A local
/// file is read where it already is; a remote file is served from memory or
/// from the encrypted preview cache — never from a plain file on disk.
///
/// Consumers read through <see cref="OpenRead"/>; each call is an independent
/// seekable stream, so a renderer that walks the file twice — a PDF counting
/// pages and then drawing one — opens twice rather than rewinding a shared
/// position. <see cref="LocalPath"/> is set only when the file genuinely lives
/// on this machine, for the one consumer that must hand a path to an engine;
/// nothing else should look at it.
/// </summary>
public abstract class FilePreviewContent : IDisposable
{
    public abstract long Length { get; }

    /// <summary>A new read-only seekable stream over the whole content.</summary>
    public abstract Stream OpenRead();

    /// <summary>
    /// The file's real path when it already lives on this machine, else null.
    /// A remote file never has one: producing a path for it would mean writing
    /// its bytes to disk in the clear.
    /// </summary>
    public virtual string? LocalPath => null;

    /// <summary>
    /// The whole content as one buffer, for the engine that cannot read a
    /// stream — SQLite deserializes a database from contiguous memory.
    /// </summary>
    public virtual async ValueTask<byte[]> ReadAllBytesAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = OpenRead();
        var buffer = new byte[Length];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>
    /// Content for a file that already lives on this machine: read in place,
    /// never copied, and the one kind that has a path to give out.
    /// </summary>
    public static FilePreviewContent FromLocalFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new LocalFileContent(path);
    }

    private sealed class LocalFileContent(string path) : FilePreviewContent
    {
        public override long Length => new FileInfo(path).Length;

        public override string LocalPath => path;

        public override Stream OpenRead() =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}

/// <summary>
/// Produces whole-file content for consumers a bounded byte preview cannot
/// serve — a database engine, an archive index, an image decoder.
///
/// This is the one operation that pulls a whole file, so it is bounded by an
/// explicit byte ceiling and refused rather than truncated when the file
/// exceeds it: a truncated database is not a smaller database; it is a
/// corrupt one. Downloads are kept — in memory for small files, in the
/// preview cache for large ones — so the same file selected twice is fetched
/// once.
/// </summary>
public interface IFileContentSource
{
    ValueTask<FilePanelResult<FilePreviewContent>> OpenContentAsync(
        FilePanelLocation location,
        long maximumBytes,
        CancellationToken cancellationToken);
}
