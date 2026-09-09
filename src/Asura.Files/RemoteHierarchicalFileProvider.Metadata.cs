namespace Asura.Files;

public abstract partial class RemoteHierarchicalFileProvider
{
    private async ValueTask<FileProviderResult<FilePage>> ListCoreAsync(
        FileListRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PageSize > Capabilities.Limits.MaximumListPageSize)
        {
            return Failure<FilePage>(
                FileProviderErrorCode.LimitExceeded,
                $"The requested {_protocolName} list page is too large.");
        }

        var resolved = Resolve(request.Location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FilePage>.Failure(resolved.Error!);
        }

        var directory = resolved.Value!;
        var scope = $"{ProfileId.Value}\n{Authority.Value}\n{directory.RemotePath}";
        if (request.ContinuationToken is { } continuation)
        {
            if (!_pageCursors.TryGet(continuation, out var cursor)
                || !string.Equals(cursor!.Scope, scope, StringComparison.Ordinal))
            {
                return Failure<FilePage>(
                    FileProviderErrorCode.InvalidLocation,
                    $"The {_protocolName} continuation token is invalid for this directory.");
            }

            return FileProviderResult<FilePage>.Success(PageFromSnapshot(
                cursor.Entries,
                cursor.Offset,
                request.PageSize,
                scope));
        }

        await using var session = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        var inspection = await InspectNoLinksAsync(
            session,
            directory,
            includeLeaf: true,
            cancellationToken).ConfigureAwait(false);
        if (inspection.Error is not null)
        {
            return FileProviderResult<FilePage>.Failure(inspection.Error);
        }

        var directoryEntry = inspection.Leaf;
        if (directoryEntry is null)
        {
            return Failure<FilePage>(FileProviderErrorCode.NotFound, "The remote directory was not found.");
        }

        if (directoryEntry.Kind != FileEntryKind.Directory)
        {
            return Failure<FilePage>(FileProviderErrorCode.NotDirectory, "The remote location is not a directory.");
        }

        var locationVersionError = CheckLocationVersion(request.Location, directoryEntry);
        if (locationVersionError is not null)
        {
            return FileProviderResult<FilePage>.Failure(locationVersionError);
        }

        var listed = await session.ListAsync(directory.RemotePath, cancellationToken).ConfigureAwait(false);
        var snapshot = RemoteDirectorySnapshot.Capture(listed, cancellationToken);
        var converted = new List<FileEntry>(snapshot.Count);
        foreach (var remoteEntry in snapshot.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remoteEntry.Name is "." or "..")
            {
                continue;
            }

            FilePathSegment segment;
            try
            {
                segment = new FilePathSegment(remoteEntry.Name);
            }
            catch (ArgumentException)
            {
                return Failure<FilePage>(
                    FileProviderErrorCode.IoFailure,
                    $"The {_protocolName} server returned an unrepresentable file name.");
            }

            if (remoteEntry.Name.Any(char.IsControl)
                || (!_allowBackslashSegments
                    && (remoteEntry.Name.Contains('\\') || HasBoundaryWhitespace(remoteEntry.Name)))
                || (_additionalNameValidator is not null
                    && !_additionalNameValidator(remoteEntry.Name)))
            {
                return Failure<FilePage>(
                    FileProviderErrorCode.IoFailure,
                    $"The {_protocolName} server returned an unsafe file name.");
            }

            var childLocation = new FileLocation(
                ProfileId,
                Authority,
                directory.Path.Append(segment));
            var entryResult = ToFileEntry(childLocation, remoteEntry);
            if (!entryResult.IsSuccess)
            {
                return FileProviderResult<FilePage>.Failure(entryResult.Error!);
            }

            converted.Add(entryResult.Value!);
        }

        return FileProviderResult<FilePage>.Success(PageFromSnapshot(
            converted,
            offset: 0,
            request.PageSize,
            scope));
    }

    private FilePage PageFromSnapshot(
        IReadOnlyList<FileEntry> entries,
        int offset,
        int pageSize,
        string scope)
    {
        var pageItems = entries.Skip(offset).Take(pageSize).ToArray();
        var nextOffset = offset + pageItems.Length;
        FilePageToken? next = nextOffset < entries.Count
            ? _pageCursors.Add(new RemotePageCursor(scope, entries, nextOffset))
            : null;
        return new FilePage(pageItems, next);
    }

    private async ValueTask<FileProviderResult<FileEntry>> StatCoreAsync(
        FileLocation location,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(resolved.Error!);
        }

        await using var session = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        var linkError = await EnsureNoLinksAsync(
            session,
            resolved.Value!,
            includeLeaf: false,
            cancellationToken).ConfigureAwait(false);
        if (linkError is not null)
        {
            return FileProviderResult<FileEntry>.Failure(linkError);
        }

        var remoteEntry = await session
            .StatAsync(resolved.Value!.RemotePath, cancellationToken)
            .ConfigureAwait(false);
        if (remoteEntry is null)
        {
            return Failure<FileEntry>(FileProviderErrorCode.NotFound, "The remote entry was not found.");
        }

        var versionError = CheckLocationVersion(location, remoteEntry);
        if (versionError is not null)
        {
            return FileProviderResult<FileEntry>.Failure(versionError);
        }

        return ToFileEntry(location, remoteEntry);
    }

    private async ValueTask<FileProviderError?> EnsureNoLinksAsync(
        IRemoteHierarchicalFileSession session,
        ResolvedRemotePath path,
        bool includeLeaf,
        CancellationToken cancellationToken) =>
        (await InspectNoLinksAsync(
            session,
            path,
            includeLeaf,
            cancellationToken).ConfigureAwait(false)).Error;

    private async ValueTask<RemotePathInspection> InspectNoLinksAsync(
        IRemoteHierarchicalFileSession session,
        ResolvedRemotePath path,
        bool includeLeaf,
        CancellationToken cancellationToken)
    {
        if (session.StatDetectsAnyLinkInPath)
        {
            return await InspectWholePathAsync(
                session,
                path,
                includeLeaf,
                cancellationToken).ConfigureAwait(false);
        }

        var relativeComponentsToCheck = includeLeaf
            ? path.RelativeSegments.Count
            : Math.Max(0, path.RelativeSegments.Count - 1);
        var root = await session.StatAsync("/", cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return RemotePathInspection.Failed(
                FileProviderError.Create(
                    FileProviderErrorCode.NotFound,
                    "The remote filesystem root was not found."));
        }

        if (root.Kind == FileEntryKind.Link)
        {
            return RemotePathInspection.Failed(
                FileProviderError.Create(
                    FileProviderErrorCode.LinkNotAllowed,
                    "The remote filesystem root is a symbolic link."));
        }

        if (root.Kind != FileEntryKind.Directory)
        {
            return RemotePathInspection.Failed(
                FileProviderError.Create(
                    FileProviderErrorCode.NotDirectory,
                    "The remote filesystem root is not a directory."));
        }

        var components = _remoteRootSegments
            .Concat(path.RelativeSegments.Take(relativeComponentsToCheck))
            .ToArray();
        var current = "/";
        var currentEntry = root;
        for (var index = 0; index < components.Length; index++)
        {
            current = ChildRemotePath(current, components[index]);
            var entry = await session.StatAsync(current, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return RemotePathInspection.Failed(
                    FileProviderError.Create(
                        FileProviderErrorCode.NotFound,
                        index < _remoteRootSegments.Count
                            ? "The configured remote root was not found."
                            : "A remote path component was not found."));
            }

            if (entry.Kind == FileEntryKind.Link)
            {
                return RemotePathInspection.Failed(
                    FileProviderError.Create(
                        FileProviderErrorCode.LinkNotAllowed,
                        "A remote path component is a symbolic link."));
            }

            var isIncludedLeaf = includeLeaf
                && path.RelativeSegments.Count > 0
                && index == components.Length - 1;
            if (!isIncludedLeaf && entry.Kind != FileEntryKind.Directory)
            {
                return RemotePathInspection.Failed(
                    FileProviderError.Create(
                        FileProviderErrorCode.NotDirectory,
                        "A remote path component is not a directory."));
            }

            currentEntry = entry;
        }

        return new RemotePathInspection(null, currentEntry);
    }

    private async ValueTask<RemotePathInspection> InspectWholePathAsync(
        IRemoteHierarchicalFileSession session,
        ResolvedRemotePath path,
        bool includeLeaf,
        CancellationToken cancellationToken)
    {
        var inspectedPath = includeLeaf || path.RelativeSegments.Count == 0
            ? path.RemotePath
            : ParentRemotePath(path.RemotePath);
        var entry = await session.StatAsync(inspectedPath, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return RemotePathInspection.Failed(
                FileProviderError.Create(
                    FileProviderErrorCode.NotFound,
                    "A remote path component was not found."));
        }

        if (entry.Kind == FileEntryKind.Link)
        {
            return RemotePathInspection.Failed(
                FileProviderError.Create(
                    FileProviderErrorCode.LinkNotAllowed,
                    "A remote path component is a symbolic link."));
        }

        if (!includeLeaf && entry.Kind != FileEntryKind.Directory)
        {
            return RemotePathInspection.Failed(
                FileProviderError.Create(
                    FileProviderErrorCode.NotDirectory,
                    "A remote path component is not a directory."));
        }

        return new RemotePathInspection(null, includeLeaf ? entry : null);
    }

    private static string ParentRemotePath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private sealed record RemotePathInspection(
        FileProviderError? Error,
        RemoteFileEntry? Leaf)
    {
        public static RemotePathInspection Failed(FileProviderError error) =>
            new(error, null);
    }

    private FileProviderError? CheckLocationVersion(
        FileLocation location,
        RemoteFileEntry remoteEntry)
    {
        if (location.Version is null)
        {
            return null;
        }

        var version = CreateVersion(remoteEntry);
        if (!version.IsSuccess)
        {
            return version.Error;
        }

        return version.Value == location.Version
            ? null
            : FileProviderError.Create(
                FileProviderErrorCode.PreconditionFailed,
                "The remote location version is stale.");
    }

    private FileProviderResult<FileEntry> ToFileEntry(
        FileLocation location,
        RemoteFileEntry remoteEntry)
    {
        var version = CreateVersion(remoteEntry);
        if (!version.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(version.Error!);
        }

        return FileProviderResult<FileEntry>.Success(new FileEntry(
            location.WithVersion(version.Value),
            remoteEntry.Kind,
            remoteEntry.Kind == FileEntryKind.File ? remoteEntry.Size : null,
            remoteEntry.LastModifiedAt,
            version.Value,
            location.Path.Name is { } name
                && name.Value.StartsWith('.')));
    }

    private FileProviderResult<FileVersion> CreateVersion(RemoteFileEntry remoteEntry)
    {
        try
        {
            return FileProviderResult<FileVersion>.Success(new FileVersion(remoteEntry.Revision));
        }
        catch (ArgumentException)
        {
            return Failure<FileVersion>(
                FileProviderErrorCode.IoFailure,
                $"The {_protocolName} server returned an invalid metadata revision.");
        }
    }

    private FileProviderError? CheckPrecondition(
        FileLocation destination,
        FileMutationPrecondition requestedPrecondition,
        RemoteFileEntry? existing)
    {
        var merged = RemoteFileProviderUtilities.MergeLocationVersion(
            destination,
            requestedPrecondition);
        if (!merged.IsSuccess)
        {
            return merged.Error;
        }

        if (merged.Value is FileMutationPrecondition.VersionMatches match
            && existing is not null)
        {
            var existingVersion = CreateVersion(existing);
            if (!existingVersion.IsSuccess)
            {
                return existingVersion.Error;
            }

            if (existingVersion.Value != match.Version)
            {
                return FileProviderError.Create(
                    FileProviderErrorCode.PreconditionFailed,
                    "The remote destination version is stale.");
            }
        }

        return merged.Value switch
        {
            FileMutationPrecondition.MustNotExist when existing is not null => FileProviderError.Create(
                FileProviderErrorCode.Conflict,
                "The remote destination already exists."),
            FileMutationPrecondition.MustExist when existing is null => FileProviderError.Create(
                FileProviderErrorCode.NotFound,
                "The remote destination does not exist."),
            FileMutationPrecondition.VersionMatches when existing is null => FileProviderError.Create(
                FileProviderErrorCode.PreconditionFailed,
                "The remote destination does not exist."),
            _ => null,
        };
    }
}
