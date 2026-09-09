using System.Buffers;

namespace Asura.Files;

public abstract partial class LocalFileProvider
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

        return ExecuteFileSystemOperationAsync(
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

        return ExecuteFileSystemOperationAsync(
            token => WriteCoreAsync(request, source, progress, token),
            cancellationToken);
    }

    private async ValueTask<FileProviderResult<FileReadReceipt>> ReadCoreAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.MaximumBytes > Capabilities.Limits.MaximumReadBytes
            || request.BufferSize > Capabilities.Limits.MaximumBufferSize)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.LimitExceeded,
                "The requested read exceeds the provider's read or buffer limit.");
        }

        var resolved = ResolveLocation(request.Location, allowLeafLink: false);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileReadReceipt>.Failure(resolved.Error!);
        }

        var entryResult = ReadEntry(resolved.Value!);
        if (!entryResult.IsSuccess)
        {
            return FileProviderResult<FileReadReceipt>.Failure(entryResult.Error!);
        }

        var entry = entryResult.Value!;
        if (entry.Kind == FileEntryKind.Directory)
        {
            return Failure<FileReadReceipt>(FileProviderErrorCode.IsDirectory, "A directory cannot be read as a file.");
        }

        if (entry.Kind != FileEntryKind.File)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.LinkNotAllowed,
                "Links and special files cannot be opened by this provider.");
        }

        var fileLength = entry.Size!.Value;
        if (request.Offset > fileLength)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.RangeNotSatisfiable,
                "The requested read offset is beyond the end of the file.");
        }

        var remainingInFile = fileLength - request.Offset;
        var bytesToRead = Math.Min(remainingInFile, request.MaximumBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(request.BufferSize, (int)Math.Min(int.MaxValue, Math.Max(1, bytesToRead))));
        long bytesRead = 0;

        try
        {
            await using var file = new FileStream(
                resolved.Value!.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                request.BufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            file.Seek(request.Offset, SeekOrigin.Begin);

            while (bytesRead < bytesToRead)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, bytesToRead - bytesRead);
                var read = await file
                    .ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                bytesRead += read;
                progress?.Report(new FileTransferProgress(
                    FileTransferStage.Reading,
                    bytesRead,
                    bytesToRead));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var finalEntry = ReadEntry(resolved.Value!);
        if (!finalEntry.IsSuccess || finalEntry.Value!.Version != entry.Version)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.PreconditionFailed,
                "The source changed while it was being read.");
        }

        return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
            entry.Location,
            request.Offset,
            bytesRead,
            remainingInFile > request.MaximumBytes));
    }

    private async ValueTask<FileProviderResult<FileWriteReceipt>> WriteCoreAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.BufferSize > Capabilities.Limits.MaximumBufferSize)
        {
            return Failure<FileWriteReceipt>(
                FileProviderErrorCode.LimitExceeded,
                "The requested buffer exceeds the provider limit.");
        }

        var resolved = ResolveLocation(request.Location, allowLeafLink: true);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(resolved.Error!);
        }

        if (resolved.Value!.StructuredPath.IsRoot)
        {
            return Failure<FileWriteReceipt>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The provider root cannot be replaced with a file.");
        }

        var parentResult = ResolveParentDirectory(request.Location);
        if (!parentResult.IsSuccess)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(parentResult.Error!);
        }

        var existingResult = ReadEntryIfPresent(resolved.Value!);
        if (!existingResult.IsSuccess)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(existingResult.Error!);
        }

        var existing = existingResult.Value!.Entry;
        var targetKindError = RejectNonFileDestination(existing);
        if (targetKindError is not null)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(targetKindError);
        }

        var preconditionError = CheckPrecondition(request.Location, request.Precondition, existing);
        if (preconditionError is not null)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(preconditionError);
        }

        var temporaryPath = Path.Combine(
            parentResult.Value!.Path,
            $".asura-{Guid.NewGuid():N}.tmp");
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(request.BufferSize, Capabilities.Limits.MaximumBufferSize));
        long bytesWritten = 0;

        try
        {
            var headroom = new DiskWriteHeadroom(parentResult.Value.Path, request.ContentLength);
            await using (var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                request.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (bytesWritten < request.ContentLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(buffer.Length, request.ContentLength - bytesWritten);
                    var read = await source
                        .ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return Failure<FileWriteReceipt>(
                            FileProviderErrorCode.UnexpectedEndOfStream,
                            "The source ended before its declared content length.");
                    }

                    headroom.BeforeWrite(read);
                    await temporary
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    bytesWritten += read;
                    progress?.Report(new FileTransferProgress(
                        FileTransferStage.Writing,
                        bytesWritten,
                        request.ContentLength));
                }

                await temporary.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var commitEntryResult = ReadEntryIfPresent(resolved.Value!);
            if (!commitEntryResult.IsSuccess)
            {
                return FileProviderResult<FileWriteReceipt>.Failure(commitEntryResult.Error!);
            }

            var commitEntry = commitEntryResult.Value!.Entry;
            var commitTargetKindError = RejectNonFileDestination(commitEntry);
            if (commitTargetKindError is not null)
            {
                return FileProviderResult<FileWriteReceipt>.Failure(commitTargetKindError);
            }

            var commitPreconditionError = CheckPrecondition(
                request.Location,
                request.Precondition,
                commitEntry);
            if (commitPreconditionError is not null)
            {
                return FileProviderResult<FileWriteReceipt>.Failure(commitPreconditionError);
            }

            progress?.Report(new FileTransferProgress(
                FileTransferStage.Committing,
                bytesWritten,
                request.ContentLength));
            File.Move(temporaryPath, resolved.Value!.Path, overwrite: commitEntry is not null);

            var destination = ReadEntry(new ResolvedLocalLocation(
                request.Location.WithVersion(null),
                resolved.Value!.StructuredPath,
                resolved.Value!.Path));
            if (!destination.IsSuccess)
            {
                return FileProviderResult<FileWriteReceipt>.Failure(destination.Error!);
            }

            progress?.Report(new FileTransferProgress(
                FileTransferStage.Completed,
                bytesWritten,
                request.ContentLength));
            return FileProviderResult<FileWriteReceipt>.Success(new FileWriteReceipt(
                destination.Value!,
                bytesWritten,
                existing is not null));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private FileProviderResult<ResolvedLocalLocation> ResolveParentDirectory(FileLocation location)
    {
        if (location.Address is not FileLocationAddress.Hierarchical hierarchical)
        {
            return Failure<ResolvedLocalLocation>(
                FileProviderErrorCode.InvalidLocation,
                "The local file provider requires a hierarchical path location.");
        }

        var parentLocation = new FileLocation(
            location.ProviderProfileId,
            location.Authority,
            hierarchical.Path.Parent);
        var parent = ResolveLocation(parentLocation, allowLeafLink: false);
        if (!parent.IsSuccess)
        {
            return parent;
        }

        var parentEntry = ReadEntry(parent.Value!);
        if (!parentEntry.IsSuccess)
        {
            return FileProviderResult<ResolvedLocalLocation>.Failure(parentEntry.Error!);
        }

        return parentEntry.Value!.Kind == FileEntryKind.Directory
            ? parent
            : Failure<ResolvedLocalLocation>(
                FileProviderErrorCode.NotDirectory,
                "The destination parent is not a directory.");
    }

    private static FileProviderError? RejectNonFileDestination(FileEntry? existing) => existing?.Kind switch
    {
        FileEntryKind.Directory => FileProviderError.Create(
            FileProviderErrorCode.IsDirectory,
            "A directory cannot be replaced with a file."),
        FileEntryKind.Link => FileProviderError.Create(
            FileProviderErrorCode.LinkNotAllowed,
            "A link or reparse point cannot be replaced through a write operation."),
        FileEntryKind.Other => FileProviderError.Create(
            FileProviderErrorCode.UnsupportedCapability,
            "The destination is a special file that this provider cannot replace."),
        _ => null,
    };
}
