namespace Asura.Files;

public abstract partial class RemoteHierarchicalFileProvider
{
    private async ValueTask<FileProviderResult<FileReadReceipt>> ReadCoreAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!destination.CanWrite)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.InvalidLocation,
                "The destination stream is not writable.");
        }

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

        var resolved = Resolve(request.Location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileReadReceipt>.Failure(resolved.Error!);
        }

        await using var session = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        var linkError = await EnsureNoLinksAsync(
            session,
            resolved.Value!,
            includeLeaf: true,
            cancellationToken).ConfigureAwait(false);
        if (linkError is not null)
        {
            return FileProviderResult<FileReadReceipt>.Failure(linkError);
        }

        var entry = await session
            .StatAsync(resolved.Value!.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return Failure<FileReadReceipt>(FileProviderErrorCode.NotFound, "The remote file was not found.");
        }

        if (entry.Kind == FileEntryKind.Directory)
        {
            return Failure<FileReadReceipt>(FileProviderErrorCode.IsDirectory, "The remote location is a directory.");
        }

        if (entry.Kind == FileEntryKind.Link)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.LinkNotAllowed,
                "Symbolic links cannot be read through the file-provider boundary.");
        }

        if (entry.Kind == FileEntryKind.Other)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                "Special remote entries cannot be read through the file-provider boundary.");
        }

        var versionError = CheckLocationVersion(request.Location, entry);
        if (versionError is not null)
        {
            return FileProviderResult<FileReadReceipt>.Failure(versionError);
        }

        if (entry.Size is not { } size)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                $"The {_protocolName} server did not report a bounded file size.");
        }

        if (request.Offset > size)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.RangeNotSatisfiable,
                "The requested offset is beyond the remote file.");
        }

        var expectedBytes = Math.Min(request.MaximumBytes, size - request.Offset);
        if (expectedBytes == 0)
        {
            return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
                request.Location,
                request.Offset,
                BytesRead: 0,
                IsTruncated: false));
        }

        await using var remote = await session
            .OpenReadAsync(resolved.Value.RemotePath, request.Offset, cancellationToken)
            .ConfigureAwait(false);
        using var cancelRead = cancellationToken.Register(
            static state =>
            {
                try
                {
                    ((Stream)state!).Dispose();
                }
                catch (Exception)
                {
                    // Cancellation must remain best effort at the transport boundary.
                }
            },
            remote);
        var bytesRead = await RemoteFileProviderUtilities.CopyAtMostAsync(
            remote,
            destination,
            expectedBytes,
            request.BufferSize,
            FileTransferStage.Reading,
            progress,
            expectedBytes,
            cancellationToken).ConfigureAwait(false);
        if (bytesRead != expectedBytes)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.UnexpectedEndOfStream,
                "The remote file changed or ended during the bounded read.");
        }

        progress?.Report(new FileTransferProgress(FileTransferStage.Completed, bytesRead, expectedBytes));
        return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
            request.Location,
            request.Offset,
            bytesRead,
            request.Offset + bytesRead < size));
    }

    private async ValueTask<FileProviderResult<FileWriteReceipt>> WriteCoreAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!source.CanRead)
        {
            return Failure<FileWriteReceipt>(
                FileProviderErrorCode.InvalidLocation,
                "The source stream is not readable.");
        }

        var limitError = RemoteFileProviderUtilities.ValidateBufferSize(
            request.BufferSize,
            Capabilities.Limits.MaximumBufferSize);
        if (limitError is not null)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(limitError);
        }

        var resolved = Resolve(request.Location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(resolved.Error!);
        }

        if (resolved.Value!.Path.IsRoot)
        {
            return Failure<FileWriteReceipt>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The configured remote root cannot be replaced with a file.");
        }

        await using var session = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        var linkError = await EnsureNoLinksAsync(
            session,
            resolved.Value,
            includeLeaf: false,
            cancellationToken).ConfigureAwait(false);
        if (linkError is not null)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(linkError);
        }

        var existing = await session
            .StatAsync(resolved.Value.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        if (existing?.Kind == FileEntryKind.Directory)
        {
            return Failure<FileWriteReceipt>(FileProviderErrorCode.IsDirectory, "The destination is a directory.");
        }

        if (existing?.Kind == FileEntryKind.Link)
        {
            return Failure<FileWriteReceipt>(
                FileProviderErrorCode.LinkNotAllowed,
                "A symbolic link cannot be replaced through a write.");
        }

        if (existing?.Kind == FileEntryKind.Other)
        {
            return Failure<FileWriteReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                "A special remote entry cannot be replaced through a write.");
        }

        var preconditionError = CheckPrecondition(request.Location, request.Precondition, existing);
        if (preconditionError is not null)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(preconditionError);
        }

        var temporaryPath = TemporarySibling(resolved.Value);
        var temporaryExists = false;
        try
        {
            await using (var remote = await session
                .OpenCreateNewAsync(temporaryPath, cancellationToken)
                .ConfigureAwait(false))
            {
                temporaryExists = true;
                using var exactSource = new ExactLengthReadStream(
                    source,
                    request.ContentLength,
                    transferred => progress?.Report(new FileTransferProgress(
                        FileTransferStage.Writing,
                        transferred,
                        request.ContentLength)));
                var bytesWritten = await RemoteFileProviderUtilities.CopyAtMostAsync(
                    exactSource,
                    remote,
                    request.ContentLength,
                    request.BufferSize,
                    FileTransferStage.Writing,
                    progress: null,
                    request.ContentLength,
                    cancellationToken).ConfigureAwait(false);
                if (bytesWritten != request.ContentLength)
                {
                    throw new EndOfStreamException("The source ended before its declared content length.");
                }

                await remote.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new FileTransferProgress(
                FileTransferStage.Committing,
                request.ContentLength,
                request.ContentLength));
            var committed = await CommitTemporaryAsync(
                session,
                temporaryPath,
                resolved.Value,
                request.Precondition,
                cancellationToken).ConfigureAwait(false);
            if (!committed.IsSuccess)
            {
                return FileProviderResult<FileWriteReceipt>.Failure(committed.Error!);
            }

            temporaryExists = false;
            var destination = ToFileEntry(request.Location, committed.Value!.Entry);
            if (!destination.IsSuccess)
            {
                return FileProviderResult<FileWriteReceipt>.Failure(destination.Error!);
            }

            progress?.Report(new FileTransferProgress(
                FileTransferStage.Completed,
                request.ContentLength,
                request.ContentLength));
            return FileProviderResult<FileWriteReceipt>.Success(new FileWriteReceipt(
                destination.Value!,
                request.ContentLength,
                committed.Value.ReplacedExisting));
        }
        finally
        {
            if (temporaryExists)
            {
                await TryDeleteFileAsync(session, temporaryPath).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<FileProviderResult<RemoteCommit>> CommitTemporaryAsync(
        IRemoteHierarchicalFileSession session,
        string temporaryPath,
        ResolvedRemotePath destination,
        FileMutationPrecondition precondition,
        CancellationToken cancellationToken)
    {
        var current = await session
            .StatAsync(destination.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        var preconditionError = CheckPrecondition(destination.Location, precondition, current);
        if (preconditionError is not null)
        {
            return FileProviderResult<RemoteCommit>.Failure(preconditionError);
        }

        if (current?.Kind == FileEntryKind.Directory)
        {
            return Failure<RemoteCommit>(FileProviderErrorCode.IsDirectory, "The destination is a directory.");
        }

        if (current?.Kind == FileEntryKind.Link)
        {
            return Failure<RemoteCommit>(
                FileProviderErrorCode.LinkNotAllowed,
                "A symbolic link cannot be replaced by a transfer.");
        }

        if (current?.Kind == FileEntryKind.Other)
        {
            return Failure<RemoteCommit>(
                FileProviderErrorCode.UnsupportedCapability,
                "A special remote entry cannot be replaced by a transfer.");
        }

        var backupPath = current is null
            ? null
            : ChildRemotePath(
                RemoteParent(destination.RemotePath),
                $".asura-{Guid.NewGuid():N}.backup");
        if (backupPath is not null)
        {
            await session
                .RenameAsync(destination.RemotePath, backupPath, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await session
                .RenameAsync(temporaryPath, destination.RemotePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (backupPath is not null)
            {
                await TryRenameAsync(session, backupPath, destination.RemotePath).ConfigureAwait(false);
            }

            throw;
        }

        if (backupPath is not null)
        {
            await TryDeleteFileAsync(session, backupPath).ConfigureAwait(false);
        }

        var committed = await session
            .StatAsync(destination.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        if (committed is null)
        {
            return Failure<RemoteCommit>(
                FileProviderErrorCode.IoFailure,
                "The remote server did not expose the committed file.");
        }

        return FileProviderResult<RemoteCommit>.Success(
            new RemoteCommit(committed, ReplacedExisting: current is not null));
    }

    private static async ValueTask TryDeleteFileAsync(
        IRemoteHierarchicalFileSession session,
        string remotePath)
    {
        try
        {
            await session.DeleteFileAsync(remotePath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (RemoteFileSessionException)
        {
            // Best-effort cleanup must not replace the primary operation result.
        }
        catch (IOException)
        {
            // Best-effort cleanup must not replace the primary operation result.
        }
    }

    private static async ValueTask TryRenameAsync(
        IRemoteHierarchicalFileSession session,
        string sourcePath,
        string destinationPath)
    {
        try
        {
            await session
                .RenameAsync(sourcePath, destinationPath, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (RemoteFileSessionException)
        {
            // The original failure is more actionable than a best-effort rollback failure.
        }
        catch (IOException)
        {
            // The original failure is more actionable than a best-effort rollback failure.
        }
    }

    private sealed record RemoteCommit(RemoteFileEntry Entry, bool ReplacedExisting);
}
