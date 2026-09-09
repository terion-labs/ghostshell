namespace Asura.Files;

public abstract partial class RemoteHierarchicalFileProvider
{
    private async ValueTask<FileProviderResult<FileEntry>> CreateDirectoryCoreAsync(
        FileCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(request.Location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(resolved.Error!);
        }

        if (resolved.Value!.Path.IsRoot)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The configured remote root cannot be created or replaced.");
        }

        await using var session = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        var linkError = await EnsureNoLinksAsync(
            session,
            resolved.Value,
            includeLeaf: false,
            cancellationToken).ConfigureAwait(false);
        if (linkError is not null)
        {
            return FileProviderResult<FileEntry>.Failure(linkError);
        }

        var existing = await session
            .StatAsync(resolved.Value.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        var preconditionError = CheckPrecondition(request.Location, request.Precondition, existing);
        if (preconditionError is not null)
        {
            return FileProviderResult<FileEntry>.Failure(preconditionError);
        }

        if (existing is not null)
        {
            return existing.Kind == FileEntryKind.Directory
                ? ToFileEntry(request.Location, existing)
                : Failure<FileEntry>(
                    FileProviderErrorCode.Conflict,
                    "A non-directory remote entry already exists at the destination.");
        }

        await session
            .CreateDirectoryAsync(resolved.Value.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        var created = await session
            .StatAsync(resolved.Value.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        if (created is null || created.Kind != FileEntryKind.Directory)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.IoFailure,
                "The remote server did not expose the created directory.");
        }

        return ToFileEntry(request.Location, created);
    }

    private async ValueTask<FileProviderResult<FileEntry>> RenameCoreAsync(
        FileRenameRequest request,
        CancellationToken cancellationToken)
    {
        var sourceResult = Resolve(request.Source);
        if (!sourceResult.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(sourceResult.Error!);
        }

        var destinationResult = Resolve(request.Destination);
        if (!destinationResult.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(destinationResult.Error!);
        }

        var source = sourceResult.Value!;
        var destination = destinationResult.Value!;
        if (source.Path.IsRoot || destination.Path.IsRoot)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The configured remote root cannot be renamed or replaced.");
        }

        if (PathsMayAlias(source.RemotePath, destination.RemotePath))
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.InvalidLocation,
                "The remote rename source and destination may identify the same entry.");
        }

        if (DestinationMayBeDescendant(source, destination))
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.InvalidLocation,
                "A remote entry cannot be renamed into its own descendant.");
        }

        await using var session = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sourceLinkError = await EnsureNoLinksAsync(
            session,
            source,
            includeLeaf: false,
            cancellationToken).ConfigureAwait(false);
        if (sourceLinkError is not null)
        {
            return FileProviderResult<FileEntry>.Failure(sourceLinkError);
        }

        var destinationLinkError = await EnsureNoLinksAsync(
            session,
            destination,
            includeLeaf: false,
            cancellationToken).ConfigureAwait(false);
        if (destinationLinkError is not null)
        {
            return FileProviderResult<FileEntry>.Failure(destinationLinkError);
        }

        var sourceEntry = await session
            .StatAsync(source.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        if (sourceEntry is null)
        {
            return Failure<FileEntry>(FileProviderErrorCode.NotFound, "The remote source was not found.");
        }

        if (sourceEntry.Kind == FileEntryKind.Link)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.LinkNotAllowed,
                "A symbolic link cannot be renamed through this provider.");
        }

        if (sourceEntry.Kind == FileEntryKind.Other)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.UnsupportedCapability,
                "A special remote entry cannot be renamed through this provider.");
        }

        var sourceVersionError = CheckLocationVersion(request.Source, sourceEntry);
        if (sourceVersionError is not null)
        {
            return FileProviderResult<FileEntry>.Failure(sourceVersionError);
        }

        var destinationEntry = await session
            .StatAsync(destination.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        var destinationError = CheckPrecondition(
            request.Destination,
            request.DestinationPrecondition,
            destinationEntry);
        if (destinationError is not null)
        {
            return FileProviderResult<FileEntry>.Failure(destinationError);
        }

        if (destinationEntry?.Kind == FileEntryKind.Link)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.LinkNotAllowed,
                "A symbolic link cannot be replaced by rename.");
        }

        if (destinationEntry?.Kind == FileEntryKind.Other)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.UnsupportedCapability,
                "A special remote entry cannot be replaced by rename.");
        }

        if (destinationEntry?.Kind == FileEntryKind.Directory)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.Conflict,
                "A remote directory cannot be replaced by rename.");
        }

        var backupPath = destinationEntry is null
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
                .RenameAsync(source.RemotePath, destination.RemotePath, cancellationToken)
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

        var renamed = await session
            .StatAsync(destination.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        return renamed is null
            ? Failure<FileEntry>(
                FileProviderErrorCode.IoFailure,
                "The remote server did not expose the renamed entry.")
            : ToFileEntry(request.Destination, renamed);
    }

    private async ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteCoreAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(request.Location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileDeleteReceipt>.Failure(resolved.Error!);
        }

        if (resolved.Value!.Path.IsRoot)
        {
            return Failure<FileDeleteReceipt>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The configured remote root cannot be deleted.");
        }

        await using var session = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        var linkError = await EnsureNoLinksAsync(
            session,
            resolved.Value,
            includeLeaf: false,
            cancellationToken).ConfigureAwait(false);
        if (linkError is not null)
        {
            return FileProviderResult<FileDeleteReceipt>.Failure(linkError);
        }

        var existing = await session
            .StatAsync(resolved.Value.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        var preconditionError = CheckPrecondition(request.Location, request.Precondition, existing);
        if (preconditionError is not null)
        {
            return FileProviderResult<FileDeleteReceipt>.Failure(preconditionError);
        }

        if (existing is null)
        {
            return Failure<FileDeleteReceipt>(FileProviderErrorCode.NotFound, "The remote entry was not found.");
        }

        if (existing.Kind == FileEntryKind.Link)
        {
            return Failure<FileDeleteReceipt>(
                FileProviderErrorCode.LinkNotAllowed,
                "Symbolic-link deletion is not supported by this provider.");
        }

        if (existing.Kind == FileEntryKind.Other)
        {
            return Failure<FileDeleteReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                "Special remote entry deletion is not supported by this provider.");
        }

        if (existing.Kind == FileEntryKind.Directory)
        {
            if (request.Recursive)
            {
                await DeleteDirectoryTreeAsync(
                    session,
                    resolved.Value.RemotePath,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var children = await session
                    .ListAsync(resolved.Value.RemotePath, cancellationToken)
                    .ConfigureAwait(false);
                if (children.Count > 0)
                {
                    return Failure<FileDeleteReceipt>(
                        FileProviderErrorCode.DirectoryNotEmpty,
                        "The remote directory is not empty.");
                }

                await session
                    .DeleteDirectoryAsync(resolved.Value.RemotePath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            // Remote delete is permanent. There is no provider-neutral trash or undo contract.
            await session
                .DeleteFileAsync(resolved.Value.RemotePath, cancellationToken)
                .ConfigureAwait(false);
        }

        return FileProviderResult<FileDeleteReceipt>.Success(new FileDeleteReceipt(
            request.Location.WithVersion(null),
            existing.Kind == FileEntryKind.Directory));
    }

    private async ValueTask DeleteDirectoryTreeAsync(
        IRemoteHierarchicalFileSession session,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var children = await session.ListAsync(directoryPath, cancellationToken).ConfigureAwait(false);
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Name is "." or "..")
            {
                continue;
            }

            if (child.Name.Any(char.IsControl)
                || child.Name.Contains('/', StringComparison.Ordinal)
                || (!_allowBackslashSegments
                    && (child.Name.Contains('\\') || HasBoundaryWhitespace(child.Name)))
                || (_additionalNameValidator is not null
                    && !_additionalNameValidator(child.Name)))
            {
                throw new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.InvalidName,
                    "The remote server returned an unsafe recursive-delete name.");
            }

            var childPath = ChildRemotePath(directoryPath, child.Name);
            if (child.Kind == FileEntryKind.Link)
            {
                throw new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.LinkNotAllowed,
                    "Recursive deletion stopped at a symbolic link.");
            }

            if (child.Kind == FileEntryKind.Other)
            {
                throw new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.Unsupported,
                    "Recursive deletion stopped at a special remote entry.");
            }

            if (child.Kind == FileEntryKind.Directory)
            {
                await DeleteDirectoryTreeAsync(session, childPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await session.DeleteFileAsync(childPath, cancellationToken).ConfigureAwait(false);
            }
        }

        await session.DeleteDirectoryAsync(directoryPath, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<FileProviderResult<FileTransferReceipt>> TransferCoreAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var limitError = RemoteFileProviderUtilities.ValidateBufferSize(
            request.BufferSize,
            Capabilities.Limits.MaximumBufferSize);
        if (limitError is not null)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(limitError);
        }

        var sourceResult = Resolve(request.Source);
        if (!sourceResult.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(sourceResult.Error!);
        }

        var destinationResult = Resolve(request.Destination);
        if (!destinationResult.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(destinationResult.Error!);
        }

        var source = sourceResult.Value!;
        var destination = destinationResult.Value!;
        if (source.Path.IsRoot || destination.Path.IsRoot)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The configured remote root cannot be transferred or replaced.");
        }

        if (PathsMayAlias(source.RemotePath, destination.RemotePath))
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.InvalidLocation,
                "The remote transfer source and destination must be different paths.");
        }

        await using var transferSessions = await OpenTransferSessionsAsync(
            cancellationToken).ConfigureAwait(false);
        var sourceSession = transferSessions.Source;
        var destinationSession = transferSessions.Destination;
        var sourceLinkError = await EnsureNoLinksAsync(
            sourceSession,
            source,
            includeLeaf: true,
            cancellationToken).ConfigureAwait(false);
        if (sourceLinkError is not null)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(sourceLinkError);
        }

        var destinationLinkError = await EnsureNoLinksAsync(
            destinationSession,
            destination,
            includeLeaf: false,
            cancellationToken).ConfigureAwait(false);
        if (destinationLinkError is not null)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(destinationLinkError);
        }

        var sourceEntry = await sourceSession
            .StatAsync(source.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        if (sourceEntry is null)
        {
            return Failure<FileTransferReceipt>(FileProviderErrorCode.NotFound, "The remote source was not found.");
        }

        if (sourceEntry.Kind == FileEntryKind.Directory)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                "Directory transfer is not supported by this provider.");
        }

        if (sourceEntry.Kind == FileEntryKind.Link)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.LinkNotAllowed,
                "Symbolic links cannot be copied through the file-provider boundary.");
        }

        if (sourceEntry.Kind == FileEntryKind.Other)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                "Special remote entries cannot be copied through the file-provider boundary.");
        }

        var sourceVersionError = CheckLocationVersion(request.Source, sourceEntry);
        if (sourceVersionError is not null)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(sourceVersionError);
        }

        if (sourceEntry.Size is not { } sourceSize)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                $"The {_protocolName} server did not report a bounded source size.");
        }

        var destinationEntry = await destinationSession
            .StatAsync(destination.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        var preconditionError = CheckPrecondition(
            request.Destination,
            request.DestinationPrecondition,
            destinationEntry);
        if (preconditionError is not null)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(preconditionError);
        }

        if (destinationEntry?.Kind == FileEntryKind.Directory)
        {
            return Failure<FileTransferReceipt>(FileProviderErrorCode.IsDirectory, "The destination is a directory.");
        }

        if (destinationEntry?.Kind == FileEntryKind.Link)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.LinkNotAllowed,
                "A symbolic link cannot be replaced by transfer.");
        }

        if (destinationEntry?.Kind == FileEntryKind.Other)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                "A special remote entry cannot be replaced by transfer.");
        }

        var temporaryPath = TemporarySibling(destination);
        var temporaryExists = false;
        try
        {
            await using var remoteSource = await sourceSession
                .OpenReadAsync(source.RemotePath, offset: 0, cancellationToken)
                .ConfigureAwait(false);
            await using (var remoteDestination = await destinationSession
                .OpenCreateNewAsync(temporaryPath, cancellationToken)
                .ConfigureAwait(false))
            {
                temporaryExists = true;
                var transferred = await RemoteFileProviderUtilities.CopyAtMostAsync(
                    remoteSource,
                    remoteDestination,
                    sourceSize,
                    request.BufferSize,
                    FileTransferStage.Writing,
                    progress,
                    sourceSize,
                    cancellationToken).ConfigureAwait(false);
                if (transferred != sourceSize)
                {
                    throw new EndOfStreamException("The remote source ended during transfer.");
                }

                await remoteDestination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var currentSource = await sourceSession
                .StatAsync(source.RemotePath, cancellationToken)
                .ConfigureAwait(false);
            if (currentSource is null || !string.Equals(currentSource.Revision, sourceEntry.Revision, StringComparison.Ordinal))
            {
                return Failure<FileTransferReceipt>(
                    FileProviderErrorCode.PreconditionFailed,
                    "The remote source changed before the destination could be committed.");
            }

            var committed = await CommitTemporaryAsync(
                destinationSession,
                temporaryPath,
                destination,
                request.DestinationPrecondition,
                cancellationToken).ConfigureAwait(false);
            if (!committed.IsSuccess)
            {
                return FileProviderResult<FileTransferReceipt>.Failure(committed.Error!);
            }

            temporaryExists = false;
            var sourceDeleted = false;
            if (request.Kind == FileTransferKind.Move)
            {
                progress?.Report(new FileTransferProgress(
                    FileTransferStage.DeletingSource,
                    sourceSize,
                    sourceSize));
                try
                {
                    await sourceSession
                        .DeleteFileAsync(source.RemotePath, cancellationToken)
                        .ConfigureAwait(false);
                    sourceDeleted = true;
                }
                catch (RemoteFileSessionException exception)
                {
                    return Failure<FileTransferReceipt>(
                        FileProviderErrorCode.PartialTransfer,
                        "The destination committed, but the remote source could not be deleted.",
                        exception.Retryable);
                }
                catch (OperationCanceledException)
                {
                    return Failure<FileTransferReceipt>(
                        FileProviderErrorCode.PartialTransfer,
                        "The destination committed, but deleting the remote source was cancelled.");
                }
            }

            var destinationFile = ToFileEntry(request.Destination, committed.Value!.Entry);
            if (!destinationFile.IsSuccess)
            {
                return FileProviderResult<FileTransferReceipt>.Failure(destinationFile.Error!);
            }

            progress?.Report(new FileTransferProgress(
                FileTransferStage.Completed,
                sourceSize,
                sourceSize));
            return FileProviderResult<FileTransferReceipt>.Success(new FileTransferReceipt(
                request.Source,
                destinationFile.Value!,
                request.Kind,
                sourceSize,
                committed.Value.ReplacedExisting,
                sourceDeleted));
        }
        finally
        {
            if (temporaryExists)
            {
                await TryDeleteFileAsync(destinationSession, temporaryPath).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<RemoteFileTransferSessions> OpenTransferSessionsAsync(
        CancellationToken cancellationToken)
    {
        if (_sessions is IRemoteFileTransferSessionFactory transferFactory)
        {
            return await transferFactory
                .OpenTransferSessionsAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var source = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new RemoteFileTransferSessions(source, destination);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool PathsMayAlias(string sourcePath, string destinationPath) => string.Equals(sourcePath, destinationPath
, StringComparison.Ordinal) || (Capabilities.NameComparison != FileNameComparison.CaseSensitive
            && string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase));

    private bool DestinationMayBeDescendant(
        ResolvedRemotePath source,
        ResolvedRemotePath destination) =>
        destination.Path.IsDescendantOf(source.Path)
        || (Capabilities.NameComparison != FileNameComparison.CaseSensitive
            && destination.RemotePath.StartsWith(
                $"{source.RemotePath}/",
                StringComparison.OrdinalIgnoreCase));
}
