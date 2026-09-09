namespace Asura.Files;

public sealed partial class S3FileProvider
{
    public ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
        FileCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(Failure<FileEntry>(
            FileProviderErrorCode.UnsupportedCapability,
            "S3 object buckets do not have creatable directories."));
    }

    public ValueTask<FileProviderResult<FileEntry>> RenameAsync(
        FileRenameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(Failure<FileEntry>(
            FileProviderErrorCode.UnsupportedCapability,
            "Portable S3 rename semantics are not available across S3-compatible services."));
    }

    public ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            token => TransferCoreAsync(request, progress, token),
            cancellationToken,
            request.DestinationPrecondition);
    }

    public ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            token => DeleteCoreAsync(request, token),
            cancellationToken,
            request.Precondition);
    }

    private async ValueTask<FileProviderResult<FileTransferReceipt>> TransferCoreAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.Kind != FileTransferKind.Copy)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                "S3 move is not claimed because copy-then-delete can commit only one side.");
        }

        var limitError = RemoteFileProviderUtilities.ValidateBufferSize(
            request.BufferSize,
            Capabilities.Limits.MaximumBufferSize);
        if (limitError is not null)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(limitError);
        }

        var source = ResolveObject(request.Source);
        var destination = ResolveObject(request.Destination);
        if (!source.IsSuccess || !destination.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(source.Error ?? destination.Error!);
        }

        if (string.Equals(source.Value!.Key, destination.Value!.Key, StringComparison.Ordinal))
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.Conflict,
                "The S3 copy source and destination are the same object key.");
        }

        var sourceStat = await StatCoreAsync(request.Source, cancellationToken).ConfigureAwait(false);
        if (!sourceStat.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(sourceStat.Error!);
        }

        var sourceEntry = sourceStat.Value!;
        if (sourceEntry.Kind != FileEntryKind.File)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.IsDirectory,
                "S3 prefix copies are not supported by the object transfer contract.");
        }

        var size = sourceEntry.Size!.Value;
        var precondition = RemoteFileProviderUtilities.MergeLocationVersion(
            request.Destination,
            request.DestinationPrecondition);
        if (!precondition.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(precondition.Error!);
        }

        var replacedExisting = precondition.Value is not FileMutationPrecondition.MustNotExist;
        if (precondition.Value is FileMutationPrecondition.Any)
        {
            replacedExisting = await ObjectExistsAsync(destination.Value!.Key, cancellationToken)
                .ConfigureAwait(false);
        }

        var (ifMatch, ifNoneMatch) = MutationHeaders(precondition.Value!);
        progress?.Report(new FileTransferProgress(FileTransferStage.Committing, 0, size));
        var mutation = await _store.CopyAsync(
            _options.BucketName,
            source.Value!.Key,
            destination.Value!.Key,
            sourceEntry.Version.Value,
            ifMatch,
            ifNoneMatch,
            cancellationToken).ConfigureAwait(false);
        var destinationEntry = ObjectEntry(
            request.Destination.WithVersion(null),
            size,
            mutation.LastModifiedAt,
            mutation.ETag);
        progress?.Report(new FileTransferProgress(FileTransferStage.Completed, size, size));
        return FileProviderResult<FileTransferReceipt>.Success(new FileTransferReceipt(
            sourceEntry.Location,
            destinationEntry,
            FileTransferKind.Copy,
            size,
            replacedExisting,
            SourceDeleted: false));
    }

    private async ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteCoreAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveObject(request.Location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileDeleteReceipt>.Failure(resolved.Error!);
        }

        var stat = await StatCoreAsync(request.Location, cancellationToken).ConfigureAwait(false);
        if (!stat.IsSuccess)
        {
            return FileProviderResult<FileDeleteReceipt>.Failure(stat.Error!);
        }

        if (stat.Value!.Kind == FileEntryKind.Directory)
        {
            return Failure<FileDeleteReceipt>(
                request.Recursive
                    ? FileProviderErrorCode.UnsupportedCapability
                    : FileProviderErrorCode.DirectoryNotEmpty,
                "Deleting an S3 prefix would require an unbounded multi-object operation.");
        }

        var precondition = RemoteFileProviderUtilities.MergeLocationVersion(
            request.Location,
            request.Precondition);
        if (!precondition.IsSuccess)
        {
            return FileProviderResult<FileDeleteReceipt>.Failure(precondition.Error!);
        }

        if (precondition.Value is FileMutationPrecondition.MustNotExist)
        {
            return Failure<FileDeleteReceipt>(
                FileProviderErrorCode.Conflict,
                "The S3 object exists but the delete required it not to exist.");
        }

        var ifMatch = precondition.Value switch
        {
            FileMutationPrecondition.VersionMatches match => match.Version.Value,
            FileMutationPrecondition.MustExist => "*",
            _ => null,
        };
        await _store.DeleteAsync(
            _options.BucketName,
            resolved.Value!.Key,
            ifMatch,
            cancellationToken).ConfigureAwait(false);
        return FileProviderResult<FileDeleteReceipt>.Success(
            new FileDeleteReceipt(request.Location, WasDirectory: false));
    }
}
