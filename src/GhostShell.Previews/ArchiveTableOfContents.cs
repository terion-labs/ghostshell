using System.Formats.Tar;
using System.IO.Compression;
using GhostShell.Application;
using GhostShell.Application.Previews;

namespace GhostShell.Previews;

/// <summary>
/// Lists an archive without unpacking it. A zip is answered from its central
/// directory — the index at the end of the file — so nothing is decompressed at
/// all. A tar has no index and must be walked, but only its headers are read:
/// each entry's data is skipped rather than extracted, and nothing is written
/// to disk either way.
/// </summary>
public sealed class ArchiveTableOfContents : IArchiveTableOfContents
{
    private const long MaximumExpandedTarBytes = 64L * 1024 * 1024;
    private const long MinimumExpandedTarBytes = 1L * 1024 * 1024;
    private const long MaximumCompressionRatio = 64;

    public bool Claims(string fileName) => ArchiveFormats.IsArchive(fileName);

    public async ValueTask<IReadOnlyList<ArchiveEntryDescriptor>?> ReadAsync(
        FilePreviewContent content,
        string fileName,
        int maximumEntries,
        CancellationToken cancellationToken,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        try
        {
            return ArchiveFormats.Kind(fileName) switch
            {
                ArchiveKind.Zip => await Task.Run(
                    () => ReadZip(content, maximumEntries, offset, cancellationToken),
                    cancellationToken).ConfigureAwait(false),
                ArchiveKind.Tar => await ReadTarAsync(
                    content,
                    compressed: false,
                    maximumEntries,
                    offset,
                    cancellationToken).ConfigureAwait(false),
                ArchiveKind.CompressedTar => await ReadTarAsync(
                    content,
                    compressed: true,
                    maximumEntries,
                    offset,
                    cancellationToken).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A file that is not the archive its name claims, or one truncated
            // in transit, is a preview that cannot be shown — not a crash.
            return null;
        }
    }

    private static IReadOnlyList<ArchiveEntryDescriptor> ReadZip(
        FilePreviewContent content,
        int maximumEntries,
        int offset,
        CancellationToken cancellationToken)
    {
        // The pinned compatibility adapter bypasses SharpCompress's retaining Entries
        // collection, so skipped headers are collectible even on deep pages.
        // Parsing still uses its ZIP64/encryption-aware central-directory reader.
        using var source = content.OpenRead();
        using var archive = (SharpCompress.Archives.Zip.ZipArchive)SharpCompress.Archives.Zip.ZipArchive.OpenArchive(source);
        var entries = new List<ArchiveEntryDescriptor>();
        using var iterator = archive.EnumerateEntriesUncached().GetEnumerator();
        while (entries.Count < maximumEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!iterator.MoveNext())
            {
                break;
            }
            if (offset > 0)
            {
                offset--;
                continue;
            }
            var entry = iterator.Current;

            // A zip records a folder as an entry ending in a separator, with no
            // content of its own.
            var name = entry.Key ?? throw new InvalidDataException("A ZIP entry has no path.");
            var isDirectory = entry.IsDirectory;
            entries.Add(new ArchiveEntryDescriptor(
                name,
                isDirectory,
                isDirectory ? null : entry.Size,
                isDirectory ? null : entry.CompressedSize));
        }

        return entries;
    }

    private static async ValueTask<IReadOnlyList<ArchiveEntryDescriptor>> ReadTarAsync(
        FilePreviewContent content,
        bool compressed,
        int maximumEntries,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var file = content.OpenRead();
        await using var source = compressed
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        await using var boundedSource = compressed
            ? new ExpandedReadLimitStream(
                source,
                ExpandedTarBudget(content.Length),
                cancellationToken,
                leaveOpen: true)
            : null;
        await using var reader = new TarReader(
            boundedSource ?? source,
            leaveOpen: true);
        var entries = new List<ArchiveEntryDescriptor>();
        while (entries.Count < maximumEntries)
        {
            var entry = await reader.GetNextEntryAsync(
                copyData: false,
                cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                break;
            }

            if (offset > 0)
            {
                offset--;
                continue;
            }

            var isDirectory = entry.EntryType is TarEntryType.Directory;
            entries.Add(new ArchiveEntryDescriptor(
                entry.Name,
                isDirectory,
                isDirectory ? null : entry.Length,
                CompressedSize: null));
        }

        return entries;
    }

    private static long ExpandedTarBudget(long compressedBytes)
    {
        var ratioBudget = compressedBytes >= MaximumExpandedTarBytes / MaximumCompressionRatio
            ? MaximumExpandedTarBytes
            : compressedBytes * MaximumCompressionRatio;
        return Math.Clamp(
            ratioBudget,
            MinimumExpandedTarBytes,
            MaximumExpandedTarBytes);
    }

    /// <summary>
    /// Counts bytes after gzip and before TAR parsing. It never reads beyond
    /// the configured allowance except for one byte used to distinguish an
    /// exact-limit EOF from amplified output.
    /// </summary>
    private sealed class ExpandedReadLimitStream(
        Stream source,
        long maximumBytes,
        CancellationToken operationCancellation,
        bool leaveOpen) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            operationCancellation.ThrowIfCancellationRequested();
            var allowed = AllowedReadCount(count);
            var read = allowed == 0
                ? source.ReadByte() == -1 ? 0 : throw LimitExceeded()
                : source.Read(buffer, offset, allowed);
            _bytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            operationCancellation.ThrowIfCancellationRequested();
            var allowed = AllowedReadCount(buffer.Length);
            var read = allowed == 0
                ? source.ReadByte() == -1 ? 0 : throw LimitExceeded()
                : source.Read(buffer[..allowed]);
            _bytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            operationCancellation.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            var allowed = AllowedReadCount(buffer.Length);
            if (allowed == 0)
            {
                var probe = new byte[1];
                var probed = await source.ReadAsync(
                    probe,
                    cancellationToken).ConfigureAwait(false);
                return probed == 0 ? 0 : throw LimitExceeded();
            }

            var read = await source.ReadAsync(
                buffer[..allowed],
                cancellationToken).ConfigureAwait(false);
            _bytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                source.Dispose();
            }

            base.Dispose(disposing);
        }

        private int AllowedReadCount(int requested)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(requested);
            var remaining = maximumBytes - _bytesRead;
            return remaining <= 0
                ? 0
                : (int)Math.Min(requested, remaining);
        }

        private static InvalidDataException LimitExceeded() =>
            new("The compressed TAR expands beyond the supported listing budget.");
    }
}
