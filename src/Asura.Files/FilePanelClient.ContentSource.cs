using System.Security.Cryptography;
using System.Text;
using Asura.Application;

namespace Asura.Files;

public sealed partial class FilePanelClient : IFileContentSource
{
    public async ValueTask<FilePanelResult<FilePreviewContent>> OpenContentAsync(
        FilePanelLocation location,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!TryResolve(location, out var registration, out var providerLocation, out var error))
        {
            return FilePanelResult<FilePreviewContent>.Failure(error!);
        }

        // A local provider already has the file on disk. Copying it would
        // waste the space and, worse, hand back a stale snapshot of a file
        // the user can see changing in the same panel.
        if (registration!.Provider is ILocalFilePathSource localSource
            && localSource.TryGetLocalPath(providerLocation!) is { } localPath)
        {
            return FilePanelResult<FilePreviewContent>.Success(
                FilePreviewContent.FromLocalFile(localPath));
        }

        var stat = await registration.Provider
            .StatAsync(new FileStatRequest(providerLocation!), cancellationToken)
            .ConfigureAwait(false);
        if (!stat.IsSuccess)
        {
            return FilePanelResult<FilePreviewContent>.Failure(MapError(stat.Error!));
        }

        var entry = stat.Value!;
        if (entry.Size is { } size && size > maximumBytes)
        {
            return Failure<FilePreviewContent>(
                FilePanelErrorCode.LimitExceeded,
                "file_content_limit_exceeded",
                $"This file is {size} bytes; opening it here is limited to {maximumBytes} bytes.");
        }

        var key = ResolveContentKey(location, entry);
        if (key is not null && _contentCache?.TryGet(key) is { } cached)
        {
            return FilePanelResult<FilePreviewContent>.Success(cached);
        }

        return await DownloadContentAsync(
                registration,
                providerLocation!,
                key,
                entry.Size,
                maximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The cache key for this exact version of this file, or null when the
    /// provider offers nothing that identifies a version — with no way to
    /// tell a changed file from an unchanged one, a cache would serve stale
    /// content, so those files are downloaded every time instead.
    /// </summary>
    private static string? ResolveContentKey(FilePanelLocation location, FileEntry entry)
    {
        var version = entry.Version.Value;
        if (string.IsNullOrEmpty(version) && entry.Size is null && entry.LastModifiedAt is null)
        {
            return null;
        }

        // The key is a hash so the cache never spells out where the user's
        // files came from or what they are called.
        var identity = string.Join(
            ' ',
            location.ProviderProfileId,
            location.Authority ?? string.Empty,
            location.Address switch
            {
                FilePanelAddress.Hierarchical hierarchical =>
                    string.Join('/', hierarchical.Path.Segments),
                FilePanelAddress.ObjectKey objectKey => objectKey.Key,
                _ => "<root>",
            },
            version,
            entry.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            entry.LastModifiedAt?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }

    private async ValueTask<FilePanelResult<FilePreviewContent>> DownloadContentAsync(
        FileProviderRegistration registration,
        FileLocation location,
        string? key,
        long? sizeHint,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        // Without a cache there is nowhere for a large file to stream to, so
        // everything is held in memory — the fallback for tests and hosts
        // that never constructed one, all of which stay within small bounds.
        using var pending = _contentCache?.BeginPut(key, sizeHint);
        Stream destination;
        MemoryStream? buffered = null;
        try
        {
            destination = pending?.Destination ?? (buffered = new MemoryStream());
        }
        catch (IOException)
        {
            return Failure<FilePreviewContent>(
                FilePanelErrorCode.IoFailure,
                "file_content_store_failed",
                "The file could not be kept in the preview cache.");
        }

        await using var bufferedLifetime = buffered;
        var limits = registration.Provider.Capabilities.Limits;
        var chunk = Math.Max(1, Math.Min(limits.MaximumReadBytes, maximumBytes));
        var bufferSize = (int)Math.Min(64 * 1024, limits.MaximumBufferSize);
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = maximumBytes - offset;
            if (remaining <= 0)
            {
                // The ceiling was reached with bytes still to come: the copy
                // would be a prefix of the file, which for any structured
                // format is worse than no file at all. The pending put is
                // dropped uncommitted, so nothing of it can be found later.
                return Failure<FilePreviewContent>(
                    FilePanelErrorCode.LimitExceeded,
                    "file_content_limit_exceeded",
                    $"Opening this file here is limited to {maximumBytes} bytes.");
            }

            var read = await registration.Provider
                .ReadAsync(
                    new FileReadRequest(
                        location,
                        offset,
                        Math.Min(chunk, remaining),
                        bufferSize),
                    destination,
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return FilePanelResult<FilePreviewContent>.Failure(MapError(read.Error!));
            }

            var bytesRead = read.Value!.BytesRead;
            if (bytesRead <= 0)
            {
                break;
            }

            offset += bytesRead;
        }

        try
        {
            return FilePanelResult<FilePreviewContent>.Success(
                pending?.Commit() ?? new TransientContent(buffered!.ToArray()));
        }
        catch (IOException)
        {
            return Failure<FilePreviewContent>(
                FilePanelErrorCode.IoFailure,
                "file_content_store_failed",
                "The file could not be kept in the preview cache.");
        }
    }

    /// <summary>Content held directly, outside any cache.</summary>
    private sealed class TransientContent(byte[] bytes) : FilePreviewContent
    {
        public override long Length => bytes.Length;

        public override Stream OpenRead() => new MemoryStream(bytes, writable: false);

        public override ValueTask<byte[]> ReadAllBytesAsync(
            CancellationToken cancellationToken) => new(bytes);
    }
}

/// <summary>
/// Implemented by providers whose locations are real filesystem paths, so a
/// consumer needing a path gets the file itself rather than a copy. The
/// provider stays the authority on path composition and root confinement.
/// </summary>
internal interface ILocalFilePathSource
{
    string? TryGetLocalPath(FileLocation location);
}
