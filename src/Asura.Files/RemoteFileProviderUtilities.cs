using System.Buffers;

namespace Asura.Files;

/// <summary>
/// Shared mechanics for remote adapters. Protocol decisions remain in each provider; this type
/// only centralizes streaming validation and the location-version invariant imposed by
/// <see cref="IFileProvider"/>.
/// </summary>
internal static class RemoteFileProviderUtilities
{
    public static FileProviderError? ValidateBufferSize(
        int requestedBufferSize,
        int maximumBufferSize)
    {
        return requestedBufferSize > maximumBufferSize
            ? FileProviderError.Create(
                FileProviderErrorCode.LimitExceeded,
                $"The requested buffer exceeds the provider limit of {maximumBufferSize} bytes.")
            : null;
    }

    public static FileProviderResult<FileMutationPrecondition> MergeLocationVersion(
        FileLocation location,
        FileMutationPrecondition precondition)
    {
        if (location.Version is not { } locationVersion)
        {
            return FileProviderResult<FileMutationPrecondition>.Success(precondition);
        }

        if (precondition is FileMutationPrecondition.Any)
        {
            return FileProviderResult<FileMutationPrecondition>.Success(
                new FileMutationPrecondition.VersionMatches(locationVersion));
        }

        if (precondition is FileMutationPrecondition.VersionMatches match
            && match.Version == locationVersion)
        {
            return FileProviderResult<FileMutationPrecondition>.Success(precondition);
        }

        return Failure<FileMutationPrecondition>(
            FileProviderErrorCode.InvalidLocation,
            "A versioned destination requires the same version-match precondition.");
    }

    public static async ValueTask<long> CopyAtMostAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        int bufferSize,
        FileTransferStage stage,
        IProgress<FileTransferProgress>? progress,
        long? totalBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long bytesCopied = 0;
        try
        {
            while (bytesCopied < maximumBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, maximumBytes - bytesCopied);
                var read = await source
                    .ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                bytesCopied += read;
                progress?.Report(new FileTransferProgress(stage, bytesCopied, totalBytes));
            }

            return bytesCopied;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask SkipAsync(
        Stream source,
        long bytesToSkip,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long skipped = 0;
        try
        {
            while (skipped < bytesToSkip)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, bytesToSkip - skipped);
                var read = await source
                    .ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The remote response ended before the requested offset.");
                }

                skipped += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static FileProviderResult<T> Failure<T>(
        FileProviderErrorCode code,
        string message,
        bool retryable = false) =>
        FileProviderResult<T>.Failure(FileProviderError.Create(code, message, retryable));
}
