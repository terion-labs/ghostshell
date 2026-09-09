namespace Asura.Files;

public sealed partial class S3FileProvider
{
    public ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The read destination stream must be writable.", nameof(destination));
        }

        return ExecuteAsync(
            token => ReadCoreAsync(request, destination, progress, token),
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The write source stream must be readable.", nameof(source));
        }

        return ExecuteAsync(
            token => WriteCoreAsync(request, source, progress, token),
            cancellationToken,
            request.Precondition);
    }

    private async ValueTask<FileProviderResult<FileReadReceipt>> ReadCoreAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var limitError = RemoteFileProviderUtilities.ValidateBufferSize(
            request.BufferSize,
            Capabilities.Limits.MaximumBufferSize);
        if (request.MaximumBytes > Capabilities.Limits.MaximumReadBytes)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.LimitExceeded,
                "The requested read exceeds the provider's bounded-read limit.");
        }

        if (limitError is not null)
        {
            return FileProviderResult<FileReadReceipt>.Failure(limitError);
        }

        var stat = await StatCoreAsync(request.Location, cancellationToken).ConfigureAwait(false);
        if (!stat.IsSuccess)
        {
            return FileProviderResult<FileReadReceipt>.Failure(stat.Error!);
        }

        var entry = stat.Value!;
        if (entry.Kind == FileEntryKind.Directory)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.IsDirectory,
                "An S3 prefix cannot be read as an object.");
        }

        var size = entry.Size!.Value;
        if (request.Offset > size)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.RangeNotSatisfiable,
                "The requested read offset is beyond the end of the S3 object.");
        }

        var remaining = size - request.Offset;
        var bytesToRead = Math.Min(remaining, request.MaximumBytes);
        if (bytesToRead == 0)
        {
            return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
                entry.Location,
                request.Offset,
                BytesRead: 0,
                IsTruncated: false));
        }

        var resolved = ResolveObject(entry.Location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileReadReceipt>.Failure(resolved.Error!);
        }

        await using var response = await _store.ReadAsync(
            _options.BucketName,
            resolved.Value!.Key,
            request.Offset,
            request.Offset + bytesToRead - 1,
            entry.Version.Value,
            cancellationToken).ConfigureAwait(false);
        if (response.ContentLength != bytesToRead || !string.Equals(response.ETag, entry.Version.Value, StringComparison.Ordinal))
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.PreconditionFailed,
                "The S3 object changed before its requested range was read.");
        }

        var copied = await RemoteFileProviderUtilities.CopyAtMostAsync(
            response.Content,
            destination,
            bytesToRead,
            Math.Min(request.BufferSize, Capabilities.Limits.MaximumBufferSize),
            FileTransferStage.Reading,
            progress,
            bytesToRead,
            cancellationToken).ConfigureAwait(false);
        if (copied != bytesToRead)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.UnexpectedEndOfStream,
                "The S3 range response ended before all declared bytes were received.");
        }

        return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
            entry.Location,
            request.Offset,
            copied,
            remaining > request.MaximumBytes));
    }

    private async ValueTask<FileProviderResult<FileWriteReceipt>> WriteCoreAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var limitError = RemoteFileProviderUtilities.ValidateBufferSize(
            request.BufferSize,
            Capabilities.Limits.MaximumBufferSize);
        if (limitError is not null)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(limitError);
        }

        var resolved = ResolveObject(request.Location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(resolved.Error!);
        }

        var precondition = RemoteFileProviderUtilities.MergeLocationVersion(
            request.Location,
            request.Precondition);
        if (!precondition.IsSuccess)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(precondition.Error!);
        }

        var replacedExisting = precondition.Value is not FileMutationPrecondition.MustNotExist;
        if (precondition.Value is FileMutationPrecondition.Any)
        {
            replacedExisting = await ObjectExistsAsync(resolved.Value!.Key, cancellationToken)
                .ConfigureAwait(false);
        }

        var (ifMatch, ifNoneMatch) = MutationHeaders(precondition.Value!);
        using var exactSource = new ExactLengthReadStream(
            source,
            request.ContentLength,
            bytes => progress?.Report(new FileTransferProgress(
                FileTransferStage.Writing,
                bytes,
                request.ContentLength)));
        var mutation = await _store.WriteAsync(
            _options.BucketName,
            resolved.Value!.Key,
            exactSource,
            request.ContentLength,
            ifMatch,
            ifNoneMatch,
            cancellationToken).ConfigureAwait(false);
        if (exactSource.Position != request.ContentLength)
        {
            return Failure<FileWriteReceipt>(
                FileProviderErrorCode.UnexpectedEndOfStream,
                "The S3 transport did not consume the declared object content.");
        }

        var entry = ObjectEntry(
            request.Location.WithVersion(null),
            request.ContentLength,
            mutation.LastModifiedAt,
            mutation.ETag);
        progress?.Report(new FileTransferProgress(
            FileTransferStage.Completed,
            request.ContentLength,
            request.ContentLength));
        return FileProviderResult<FileWriteReceipt>.Success(new FileWriteReceipt(
            entry,
            request.ContentLength,
            replacedExisting));
    }

    private async ValueTask<bool> ObjectExistsAsync(
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            await _store.HeadAsync(
                _options.BucketName,
                key,
                etagToMatch: null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (S3StoreException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static (string? IfMatch, string? IfNoneMatch) MutationHeaders(
        FileMutationPrecondition precondition) => precondition switch
        {
            FileMutationPrecondition.Any => (null, null),
            FileMutationPrecondition.MustNotExist => (null, "*"),
            FileMutationPrecondition.MustExist => ("*", null),
            FileMutationPrecondition.VersionMatches match => (match.Version.Value, null),
            _ => throw new ArgumentOutOfRangeException(nameof(precondition), precondition, null),
        };
}
