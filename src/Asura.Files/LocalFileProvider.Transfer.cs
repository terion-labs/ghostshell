using System.Buffers;

namespace Asura.Files;

public abstract partial class LocalFileProvider
{
    public ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteFileSystemOperationAsync(
            token => TransferCoreAsync(request, progress, token),
            cancellationToken);
    }

    private async ValueTask<FileProviderResult<FileTransferReceipt>> TransferCoreAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.BufferSize > Capabilities.Limits.MaximumBufferSize)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.LimitExceeded,
                "The requested buffer exceeds the provider limit.");
        }

        var source = ResolveLocation(request.Source, allowLeafLink: false);
        var destination = ResolveLocation(request.Destination, allowLeafLink: true);
        if (!source.IsSuccess || !destination.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(
                source.Error ?? destination.Error!);
        }

        if (source.Value!.StructuredPath.IsRoot || destination.Value!.StructuredPath.IsRoot)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "A transfer cannot move or replace the configured provider root.");
        }

        if (PathsEqual(source.Value!.Path, destination.Value!.Path))
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.Conflict,
                "The transfer source and destination are the same location.");
        }

        var sourceEntryResult = ReadEntry(source.Value!);
        if (!sourceEntryResult.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(sourceEntryResult.Error!);
        }

        var sourceEntry = sourceEntryResult.Value!;
        if (sourceEntry.Kind is FileEntryKind.Link or FileEntryKind.Other)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.LinkNotAllowed,
                "Links, reparse points, and special files cannot be transferred.");
        }

        if (sourceEntry.Kind == FileEntryKind.Directory
            && IsWithinPath(destination.Value!.Path, source.Value!.Path))
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.InvalidLocation,
                "A directory cannot be transferred into one of its descendants.");
        }

        var destinationParent = ResolveParentDirectory(request.Destination);
        if (!destinationParent.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(destinationParent.Error!);
        }

        var existingResult = ReadEntryIfPresent(destination.Value!);
        if (!existingResult.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(existingResult.Error!);
        }

        var existing = existingResult.Value!.Entry;
        var destinationError = ValidateTransferDestination(sourceEntry.Kind, existing);
        if (destinationError is not null)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(destinationError);
        }

        var preconditionError = CheckPrecondition(
            request.Destination,
            request.DestinationPrecondition,
            existing);
        if (preconditionError is not null)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(preconditionError);
        }

        var measured = MeasureEntry(source.Value!.Path, sourceEntry.Kind, cancellationToken);
        if (!measured.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(measured.Error!);
        }

        var totalBytes = measured.Value;
        var temporaryPath = Path.Combine(
            destinationParent.Value!.Path,
            sourceEntry.Kind == FileEntryKind.Directory
                ? $".asura-{Guid.NewGuid():N}.dir"
                : $".asura-{Guid.NewGuid():N}.tmp");
        long bytesTransferred = 0;

        try
        {
            var copyResult = sourceEntry.Kind == FileEntryKind.Directory
                ? await CopyDirectoryAsync(
                    source.Value!.Path,
                    temporaryPath,
                    request.BufferSize,
                    totalBytes,
                    progress,
                    cancellationToken).ConfigureAwait(false)
                : await CopyFileAsync(
                    source.Value!.Path,
                    temporaryPath,
                    request.BufferSize,
                    totalBytes,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            if (!copyResult.IsSuccess)
            {
                return FileProviderResult<FileTransferReceipt>.Failure(copyResult.Error!);
            }

            bytesTransferred = copyResult.Value;
            cancellationToken.ThrowIfCancellationRequested();

            var currentSource = ReadEntry(source.Value!);
            if (!currentSource.IsSuccess || currentSource.Value!.Version != sourceEntry.Version)
            {
                return Failure<FileTransferReceipt>(
                    FileProviderErrorCode.PreconditionFailed,
                    "The transfer source changed before the destination could be committed.");
            }

            var commitExisting = ReadEntryIfPresent(destination.Value!);
            if (!commitExisting.IsSuccess)
            {
                return FileProviderResult<FileTransferReceipt>.Failure(commitExisting.Error!);
            }

            var commitEntry = commitExisting.Value!.Entry;
            var commitDestinationError = ValidateTransferDestination(
                sourceEntry.Kind,
                commitEntry);
            if (commitDestinationError is not null)
            {
                return FileProviderResult<FileTransferReceipt>.Failure(commitDestinationError);
            }

            var commitPreconditionError = CheckPrecondition(
                request.Destination,
                request.DestinationPrecondition,
                commitEntry);
            if (commitPreconditionError is not null)
            {
                return FileProviderResult<FileTransferReceipt>.Failure(commitPreconditionError);
            }

            progress?.Report(new FileTransferProgress(
                FileTransferStage.Committing,
                bytesTransferred,
                totalBytes));
            if (sourceEntry.Kind == FileEntryKind.Directory)
            {
                Directory.Move(temporaryPath, destination.Value!.Path);
            }
            else
            {
                File.Move(
                    temporaryPath,
                    destination.Value!.Path,
                    overwrite: commitEntry is not null);
            }

            var sourceDeleted = false;
            if (request.Kind == FileTransferKind.Move)
            {
                progress?.Report(new FileTransferProgress(
                    FileTransferStage.DeletingSource,
                    bytesTransferred,
                    totalBytes));
                var deleteError = DeleteTransferredSource(
                    source.Value!.Path,
                    sourceEntry.Kind,
                    cancellationToken);
                if (deleteError is not null)
                {
                    return FileProviderResult<FileTransferReceipt>.Failure(deleteError);
                }

                sourceDeleted = true;
            }

            var destinationEntry = ReadEntry(new ResolvedLocalLocation(
                request.Destination.WithVersion(null),
                destination.Value!.StructuredPath,
                destination.Value!.Path));
            if (!destinationEntry.IsSuccess)
            {
                return FileProviderResult<FileTransferReceipt>.Failure(destinationEntry.Error!);
            }

            progress?.Report(new FileTransferProgress(
                FileTransferStage.Completed,
                bytesTransferred,
                totalBytes));
            return FileProviderResult<FileTransferReceipt>.Success(new FileTransferReceipt(
                sourceEntry.Location,
                destinationEntry.Value!,
                request.Kind,
                bytesTransferred,
                existing is not null,
                sourceDeleted));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            else if (Directory.Exists(temporaryPath))
            {
                DeleteTree(temporaryPath, CancellationToken.None);
            }
        }
    }

    private async ValueTask<FileProviderResult<long>> CopyFileAsync(
        string sourcePath,
        string destinationPath,
        int bufferSize,
        long totalBytes,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(bufferSize, Capabilities.Limits.MaximumBufferSize));
        long bytesTransferred = 0;

        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (bytesTransferred < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, totalBytes - bytesTransferred);
                var read = await source
                    .ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return Failure<long>(
                        FileProviderErrorCode.UnexpectedEndOfStream,
                        "The source changed size while it was being transferred.");
                }

                await destination
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                bytesTransferred += read;
                progress?.Report(new FileTransferProgress(
                    FileTransferStage.Writing,
                    bytesTransferred,
                    totalBytes));
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return FileProviderResult<long>.Success(bytesTransferred);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<FileProviderResult<long>> CopyDirectoryAsync(
        string sourcePath,
        string destinationPath,
        int bufferSize,
        long totalBytes,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);
        long bytesTransferred = 0;

        foreach (var childPath in Directory.EnumerateFileSystemEntries(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(childPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Failure<long>(
                    FileProviderErrorCode.LinkNotAllowed,
                    "A directory containing a link or reparse point cannot be transferred.");
            }

            var childDestination = Path.Combine(destinationPath, Path.GetFileName(childPath));
            FileProviderResult<long> childResult;
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                childResult = await CopyDirectoryAsync(
                    childPath,
                    childDestination,
                    bufferSize,
                    totalBytes - bytesTransferred,
                    progress: null,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var childLength = new FileInfo(childPath).Length;
                childResult = await CopyFileAsync(
                    childPath,
                    childDestination,
                    bufferSize,
                    childLength,
                    progress: null,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!childResult.IsSuccess)
            {
                return childResult;
            }

            bytesTransferred += childResult.Value;
            progress?.Report(new FileTransferProgress(
                FileTransferStage.Writing,
                bytesTransferred,
                totalBytes));
        }

        return FileProviderResult<long>.Success(bytesTransferred);
    }

    private FileProviderResult<long> MeasureEntry(
        string path,
        FileEntryKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == FileEntryKind.File)
        {
            return FileProviderResult<long>.Success(new FileInfo(path).Length);
        }

        long totalBytes = 0;
        foreach (var childPath in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(childPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Failure<long>(
                    FileProviderErrorCode.LinkNotAllowed,
                    "A directory containing a link or reparse point cannot be transferred.");
            }

            var childKind = attributes.HasFlag(FileAttributes.Directory)
                ? FileEntryKind.Directory
                : FileEntryKind.File;
            var child = MeasureEntry(
                childPath,
                childKind,
                cancellationToken);
            if (!child.IsSuccess)
            {
                return child;
            }

            totalBytes += child.Value;
        }

        return FileProviderResult<long>.Success(totalBytes);
    }

    private static FileProviderError? ValidateTransferDestination(
        FileEntryKind sourceKind,
        FileEntry? destination)
    {
        if (destination?.Kind == FileEntryKind.Link)
        {
            return FileProviderError.Create(
                FileProviderErrorCode.LinkNotAllowed,
                "A transfer cannot replace a link or reparse point.");
        }

        if (sourceKind == FileEntryKind.Directory && destination is not null)
        {
            return FileProviderError.Create(
                FileProviderErrorCode.Conflict,
                "Atomic directory replacement is not supported.");
        }

        if (sourceKind == FileEntryKind.File && destination?.Kind == FileEntryKind.Directory)
        {
            return FileProviderError.Create(
                FileProviderErrorCode.IsDirectory,
                "A file transfer cannot replace a directory.");
        }

        return null;
    }

    private static FileProviderError? DeleteTransferredSource(
        string sourcePath,
        FileEntryKind sourceKind,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceKind == FileEntryKind.Directory)
            {
                DeleteTree(sourcePath, cancellationToken);
            }
            else
            {
                File.Delete(sourcePath);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return FileProviderError.Create(
                FileProviderErrorCode.PartialTransfer,
                "The destination was committed, but deleting the move source was cancelled.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return FileProviderError.Create(
                FileProviderErrorCode.PartialTransfer,
                $"The destination was committed, but the move source could not be deleted: {exception.Message}");
        }
        catch (IOException exception)
        {
            return FileProviderError.Create(
                FileProviderErrorCode.PartialTransfer,
                $"The destination was committed, but the move source could not be deleted: {exception.Message}",
                retryable: true);
        }
    }
}
