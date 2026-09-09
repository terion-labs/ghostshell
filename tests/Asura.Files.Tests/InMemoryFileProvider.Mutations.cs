namespace Asura.Files.Tests;

internal sealed partial class InMemoryFileProvider
{
    public ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
        FileCreateDirectoryRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            var locationError = ValidateMutableLocation(request.Location);
            if (locationError is not null)
            {
                return FileProviderResult<FileEntry>.Failure(locationError);
            }

            lock (_gate)
            {
                var parentError = ValidateParent(request.Location.Path);
                if (parentError is not null)
                {
                    return FileProviderResult<FileEntry>.Failure(parentError);
                }

                _nodes.TryGetValue(request.Location.Path, out var existing);
                if (existing is { Kind: not FileEntryKind.Directory })
                {
                    return Failure<FileEntry>(FileProviderErrorCode.Conflict, "A file already exists at the destination.");
                }

                var preconditionError = CheckPrecondition(request.Location, request.Precondition, existing);
                if (preconditionError is not null)
                {
                    return FileProviderResult<FileEntry>.Failure(preconditionError);
                }

                var node = existing ?? new MemoryNode(FileEntryKind.Directory, [], NextRevision());
                _nodes[request.Location.Path] = node;
                if (existing is null)
                {
                    TouchParents(request.Location.Path);
                }

                return FileProviderResult<FileEntry>.Success(ToEntry(request.Location.Path, node));
            }
        }, cancellationToken);

    public ValueTask<FileProviderResult<FileEntry>> RenameAsync(
        FileRenameRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            var sourceError = ValidateMutableLocation(request.Source);
            var destinationError = ValidateMutableLocation(request.Destination);
            if (sourceError is not null || destinationError is not null)
            {
                return FileProviderResult<FileEntry>.Failure(sourceError ?? destinationError!);
            }

            if (request.Source.Path.Equals(request.Destination.Path))
            {
                return Failure<FileEntry>(FileProviderErrorCode.Conflict, "The source and destination are the same.");
            }

            lock (_gate)
            {
                if (!_nodes.TryGetValue(request.Source.Path, out var source))
                {
                    return Failure<FileEntry>(FileProviderErrorCode.NotFound, "The source was not found.");
                }

                var sourceVersionError = CheckLocationVersion(request.Source, source);
                if (sourceVersionError is not null)
                {
                    return FileProviderResult<FileEntry>.Failure(sourceVersionError);
                }

                if (source.Kind == FileEntryKind.Directory
                    && request.Destination.Path.IsDescendantOf(request.Source.Path))
                {
                    return Failure<FileEntry>(
                        FileProviderErrorCode.InvalidLocation,
                        "A directory cannot be moved into its descendant.");
                }

                var parentError = ValidateParent(request.Destination.Path);
                if (parentError is not null)
                {
                    return FileProviderResult<FileEntry>.Failure(parentError);
                }

                _nodes.TryGetValue(request.Destination.Path, out var existing);
                if (source.Kind == FileEntryKind.Directory && existing is not null)
                {
                    return Failure<FileEntry>(
                        FileProviderErrorCode.Conflict,
                        "Directory replacement is not supported.");
                }

                if (source.Kind == FileEntryKind.File && existing?.Kind == FileEntryKind.Directory)
                {
                    return Failure<FileEntry>(FileProviderErrorCode.IsDirectory, "A file cannot replace a directory.");
                }

                var preconditionError = CheckPrecondition(
                    request.Destination,
                    request.DestinationPrecondition,
                    existing);
                if (preconditionError is not null)
                {
                    return FileProviderResult<FileEntry>.Failure(preconditionError);
                }

                var affected = _nodes
                    .Where(pair => pair.Key.Equals(request.Source.Path)
                        || pair.Key.IsDescendantOf(request.Source.Path))
                    .ToArray();
                foreach (var pair in affected)
                {
                    _nodes.Remove(pair.Key);
                }

                if (existing is not null)
                {
                    _nodes.Remove(request.Destination.Path);
                }

                foreach (var pair in affected)
                {
                    _nodes[ReplacePrefix(pair.Key, request.Source.Path, request.Destination.Path)] = pair.Value;
                }

                source.Revision = NextRevision();
                TouchParents(request.Source.Path);
                TouchParents(request.Destination.Path);
                return FileProviderResult<FileEntry>.Success(ToEntry(request.Destination.Path, source));
            }
        }, cancellationToken);

    public ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            var locationError = ValidateMutableLocation(request.Location);
            if (locationError is not null)
            {
                return FileProviderResult<FileDeleteReceipt>.Failure(locationError);
            }

            lock (_gate)
            {
                if (!_nodes.TryGetValue(request.Location.Path, out var node))
                {
                    return Failure<FileDeleteReceipt>(FileProviderErrorCode.NotFound, "The entry was not found.");
                }

                var versionError = CheckLocationVersion(request.Location, node);
                if (versionError is not null)
                {
                    return FileProviderResult<FileDeleteReceipt>.Failure(versionError);
                }

                var preconditionError = CheckPrecondition(request.Location, request.Precondition, node);
                if (preconditionError is not null)
                {
                    return FileProviderResult<FileDeleteReceipt>.Failure(preconditionError);
                }

                var descendants = _nodes.Keys
                    .Where(path => path.IsDescendantOf(request.Location.Path))
                    .ToArray();
                if (node.Kind == FileEntryKind.Directory && descendants.Length > 0 && !request.Recursive)
                {
                    return Failure<FileDeleteReceipt>(
                        FileProviderErrorCode.DirectoryNotEmpty,
                        "The directory is not empty.");
                }

                foreach (var descendant in descendants)
                {
                    _nodes.Remove(descendant);
                }

                _nodes.Remove(request.Location.Path);
                TouchParents(request.Location.Path);
                return FileProviderResult<FileDeleteReceipt>.Success(new FileDeleteReceipt(
                    request.Location,
                    node.Kind == FileEntryKind.Directory));
            }
        }, cancellationToken);

    private FileProviderError? ValidateMutableLocation(FileLocation location)
    {
        var locationError = ValidateLocation(location);
        if (locationError is not null)
        {
            return locationError;
        }

        return location.Path.IsRoot
            ? FileProviderError.Create(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The provider root cannot be mutated.")
            : null;
    }

    private static FilePath ReplacePrefix(FilePath path, FilePath source, FilePath destination)
    {
        var remaining = path.Segments.Skip(source.Segments.Length);
        return FilePath.FromSegments([.. destination.Segments, .. remaining]);
    }
}
