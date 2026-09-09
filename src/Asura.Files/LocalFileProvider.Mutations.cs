namespace Asura.Files;

public abstract partial class LocalFileProvider
{
    public ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
        FileCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteFileSystemOperationAsync(
            token =>
            {
                token.ThrowIfCancellationRequested();
                var resolved = ResolveLocation(request.Location, allowLeafLink: true);
                if (!resolved.IsSuccess)
                {
                    return ValueTask.FromResult(FileProviderResult<FileEntry>.Failure(resolved.Error!));
                }

                if (resolved.Value!.StructuredPath.IsRoot)
                {
                    return ValueTask.FromResult(Failure<FileEntry>(
                        FileProviderErrorCode.RootMutationNotAllowed,
                        "The provider root cannot be created or replaced."));
                }

                var parent = ResolveParentDirectory(request.Location);
                if (!parent.IsSuccess)
                {
                    return ValueTask.FromResult(FileProviderResult<FileEntry>.Failure(parent.Error!));
                }

                var existingResult = ReadEntryIfPresent(resolved.Value!);
                if (!existingResult.IsSuccess)
                {
                    return ValueTask.FromResult(FileProviderResult<FileEntry>.Failure(existingResult.Error!));
                }

                var existing = existingResult.Value!.Entry;
                if (existing?.Kind == FileEntryKind.Link)
                {
                    return ValueTask.FromResult(Failure<FileEntry>(
                        FileProviderErrorCode.LinkNotAllowed,
                        "A directory cannot replace a link or reparse point."));
                }

                if (existing is { Kind: not FileEntryKind.Directory })
                {
                    return ValueTask.FromResult(Failure<FileEntry>(
                        FileProviderErrorCode.Conflict,
                        "A non-directory entry already exists at the destination."));
                }

                var preconditionError = CheckPrecondition(
                    request.Location,
                    request.Precondition,
                    existing);
                if (preconditionError is not null)
                {
                    return ValueTask.FromResult(FileProviderResult<FileEntry>.Failure(preconditionError));
                }

                if (existing is null)
                {
                    var mutationError = CreateDirectoryEntry(resolved.Value!);
                    if (mutationError is not null)
                    {
                        return ValueTask.FromResult(
                            FileProviderResult<FileEntry>.Failure(mutationError));
                    }
                }

                return ValueTask.FromResult(ReadEntry(new ResolvedLocalLocation(
                    request.Location.WithVersion(null),
                    resolved.Value!.StructuredPath,
                    resolved.Value!.Path)));
            },
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileEntry>> RenameAsync(
        FileRenameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteFileSystemOperationAsync(
            token =>
            {
                token.ThrowIfCancellationRequested();
                var source = ResolveLocation(request.Source, allowLeafLink: false);
                var destination = ResolveLocation(request.Destination, allowLeafLink: true);
                if (!source.IsSuccess || !destination.IsSuccess)
                {
                    return ValueTask.FromResult(FileProviderResult<FileEntry>.Failure(
                        source.Error ?? destination.Error!));
                }

                if (source.Value!.StructuredPath.IsRoot || destination.Value!.StructuredPath.IsRoot)
                {
                    return ValueTask.FromResult(Failure<FileEntry>(
                        FileProviderErrorCode.RootMutationNotAllowed,
                        "The provider root cannot be renamed or replaced."));
                }

                if (PathsEqual(source.Value!.Path, destination.Value!.Path))
                {
                    return ValueTask.FromResult(Failure<FileEntry>(
                        FileProviderErrorCode.Conflict,
                        "The rename source and destination are the same location."));
                }

                var sourceEntry = ReadEntry(source.Value!);
                if (!sourceEntry.IsSuccess)
                {
                    return ValueTask.FromResult(FileProviderResult<FileEntry>.Failure(sourceEntry.Error!));
                }

                if (sourceEntry.Value!.Kind == FileEntryKind.Directory
                    && IsWithinPath(destination.Value!.Path, source.Value!.Path))
                {
                    return ValueTask.FromResult(Failure<FileEntry>(
                        FileProviderErrorCode.InvalidLocation,
                        "A directory cannot be renamed into one of its descendants."));
                }

                var parent = ResolveParentDirectory(request.Destination);
                if (!parent.IsSuccess)
                {
                    return ValueTask.FromResult(FileProviderResult<FileEntry>.Failure(parent.Error!));
                }

                var existingResult = ReadEntryIfPresent(destination.Value!);
                if (!existingResult.IsSuccess)
                {
                    return ValueTask.FromResult(FileProviderResult<FileEntry>.Failure(existingResult.Error!));
                }

                var existing = existingResult.Value!.Entry;
                if (existing?.Kind == FileEntryKind.Link)
                {
                    return ValueTask.FromResult(Failure<FileEntry>(
                        FileProviderErrorCode.LinkNotAllowed,
                        "A rename cannot replace a link or reparse point."));
                }

                var preconditionError = CheckPrecondition(
                    request.Destination,
                    request.DestinationPrecondition,
                    existing);
                if (preconditionError is not null)
                {
                    return ValueTask.FromResult(FileProviderResult<FileEntry>.Failure(preconditionError));
                }

                if (sourceEntry.Value!.Kind == FileEntryKind.Directory)
                {
                    if (existing is not null)
                    {
                        return ValueTask.FromResult(Failure<FileEntry>(
                            FileProviderErrorCode.Conflict,
                            "Atomic directory replacement is not supported."));
                    }

                    Directory.Move(source.Value!.Path, destination.Value!.Path);
                }
                else
                {
                    File.Move(source.Value!.Path, destination.Value!.Path, overwrite: existing is not null);
                }

                return ValueTask.FromResult(ReadEntry(new ResolvedLocalLocation(
                    request.Destination.WithVersion(null),
                    destination.Value!.StructuredPath,
                    destination.Value!.Path)));
            },
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteFileSystemOperationAsync(
            token =>
            {
                var resolved = ResolveLocation(request.Location, allowLeafLink: true);
                if (!resolved.IsSuccess)
                {
                    return ValueTask.FromResult(FileProviderResult<FileDeleteReceipt>.Failure(resolved.Error!));
                }

                if (resolved.Value!.StructuredPath.IsRoot)
                {
                    return ValueTask.FromResult(Failure<FileDeleteReceipt>(
                        FileProviderErrorCode.RootMutationNotAllowed,
                        "The configured provider root cannot be deleted."));
                }

                var entryResult = ReadEntry(resolved.Value!);
                if (!entryResult.IsSuccess)
                {
                    return ValueTask.FromResult(FileProviderResult<FileDeleteReceipt>.Failure(entryResult.Error!));
                }

                var entry = entryResult.Value!;
                var preconditionError = CheckPrecondition(
                    request.Location,
                    request.Precondition,
                    entry);
                if (preconditionError is not null)
                {
                    return ValueTask.FromResult(FileProviderResult<FileDeleteReceipt>.Failure(preconditionError));
                }

                token.ThrowIfCancellationRequested();
                var wasDirectory = entry.Kind == FileEntryKind.Directory;
                var mutationError = DeleteEntry(
                    resolved.Value!,
                    entry,
                    request.Recursive,
                    token);
                if (mutationError is not null)
                {
                    return ValueTask.FromResult(
                        FileProviderResult<FileDeleteReceipt>.Failure(mutationError));
                }

                return ValueTask.FromResult(FileProviderResult<FileDeleteReceipt>.Success(
                    new FileDeleteReceipt(request.Location, wasDirectory)));
            },
            cancellationToken);
    }

    private protected virtual FileProviderError? CreateDirectoryEntry(
        ResolvedLocalLocation resolved)
    {
        Directory.CreateDirectory(resolved.Path);
        return null;
    }

    private protected virtual FileProviderError? DeleteEntry(
        ResolvedLocalLocation resolved,
        FileEntry entry,
        bool recursive,
        CancellationToken cancellationToken)
    {
        if (entry.Kind == FileEntryKind.Link)
        {
            DeleteLink(resolved.Path);
        }
        else if (entry.Kind == FileEntryKind.Directory && recursive)
        {
            DeleteTree(resolved.Path, cancellationToken);
        }
        else if (entry.Kind == FileEntryKind.Directory)
        {
            Directory.Delete(resolved.Path);
        }
        else
        {
            File.Delete(resolved.Path);
        }

        return null;
    }

    private static void DeleteTree(string directoryPath, CancellationToken cancellationToken)
    {
        foreach (var childPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(childPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteLink(childPath);
            }
            else if (attributes.HasFlag(FileAttributes.Directory))
            {
                DeleteTree(childPath, cancellationToken);
            }
            else
            {
                File.Delete(childPath);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.Delete(directoryPath);
    }

    private static void DeleteLink(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }
}
