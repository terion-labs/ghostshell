namespace Asura.Files;

public abstract partial class LocalFileProvider
{
    public ValueTask<FileProviderResult<FilePage>> ListAsync(
        FileListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteFileSystemOperationAsync(
            async token =>
            {
                if (request.PageSize > Capabilities.Limits.MaximumListPageSize)
                {
                    return Failure<FilePage>(
                        FileProviderErrorCode.LimitExceeded,
                        "The requested page size exceeds the provider limit.");
                }

                var resolved = ResolveLocation(request.Location, allowLeafLink: false);
                if (!resolved.IsSuccess)
                {
                    return FileProviderResult<FilePage>.Failure(resolved.Error!);
                }

                var directoryEntry = ReadEntry(resolved.Value!);
                if (!directoryEntry.IsSuccess)
                {
                    return FileProviderResult<FilePage>.Failure(directoryEntry.Error!);
                }

                if (directoryEntry.Value!.Kind != FileEntryKind.Directory)
                {
                    return Failure<FilePage>(
                        FileProviderErrorCode.NotDirectory,
                        "Only directories can be listed.");
                }

                var scope = resolved.Value!.Path;
                IReadOnlyList<string> childPaths;
                var offset = 0;
                if (request.ContinuationToken is { } continuation)
                {
                    if (!_pageCursors.TryGet(continuation, out var cursor)
                        || !string.Equals(cursor!.Scope, scope, _pathComparison))
                    {
                        return InvalidPageToken();
                    }

                    childPaths = cursor.Paths;
                    offset = cursor.Offset;
                }
                else
                {
                    var snapshot = new List<string>();
                    foreach (var childPath in Directory.EnumerateFileSystemEntries(scope))
                    {
                        token.ThrowIfCancellationRequested();
                        snapshot.Add(childPath);
                    }

                    childPaths = snapshot;
                }

                var entries = new List<FileEntry>(request.PageSize);
                foreach (var childPath in childPaths.Skip(offset).Take(request.PageSize))
                {
                    token.ThrowIfCancellationRequested();
                    var childName = new FilePathSegment(Path.GetFileName(childPath));
                    var childLocation = request.Location.WithVersion(null).Child(childName);
                    var childResolved = new ResolvedLocalLocation(
                        childLocation,
                        childLocation.Path,
                        childPath);
                    var child = ReadEntry(childResolved);
                    if (!child.IsSuccess)
                    {
                        return FileProviderResult<FilePage>.Failure(child.Error!);
                    }

                    entries.Add(child.Value!);
                }

                var nextOffset = offset + entries.Count;
                FilePageToken? nextToken = nextOffset < childPaths.Count
                    ? _pageCursors.Add(new LocalPageCursor(scope, childPaths, nextOffset))
                    : null;
                await Task.CompletedTask.ConfigureAwait(false);
                return FileProviderResult<FilePage>.Success(new FilePage(entries, nextToken));
            },
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileEntry>> StatAsync(
        FileStatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteFileSystemOperationAsync(
            token =>
            {
                token.ThrowIfCancellationRequested();
                var resolved = ResolveLocation(request.Location, allowLeafLink: true);
                return ValueTask.FromResult(!resolved.IsSuccess
                    ? FileProviderResult<FileEntry>.Failure(resolved.Error!)
                    : ReadEntry(resolved.Value!));
            },
            cancellationToken);
    }

    private static FileProviderResult<FilePage> InvalidPageToken() => Failure<FilePage>(
            FileProviderErrorCode.InvalidLocation,
            "The list continuation token is invalid.");
}
