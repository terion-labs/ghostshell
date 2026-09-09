namespace Asura.Files.Tests;

internal sealed partial class InMemoryFileProvider : IFileProvider
{
    private readonly FileAuthority _authority;
    private readonly object _gate = new();
    private readonly Dictionary<FilePath, MemoryNode> _nodes = [];
    private long _revision;

    public InMemoryFileProvider(
        FileProviderProfileId profileId,
        FileAuthority authority,
        FileProviderLimits? limits = null)
    {
        ProfileId = profileId;
        _authority = authority;
        Capabilities = new FileProviderCapabilities(
            FileProviderCapability.List
            | FileProviderCapability.Stat
            | FileProviderCapability.RangedRead
            | FileProviderCapability.StreamingWrite
            | FileProviderCapability.CreateDirectory
            | FileProviderCapability.Rename
            | FileProviderCapability.Copy
            | FileProviderCapability.Move
            | FileProviderCapability.Delete
            | FileProviderCapability.AtomicReplace
            | FileProviderCapability.Pagination,
            FileNameComparison.CaseSensitive,
            limits ?? new FileProviderLimits(
                maximumListPageSize: 100,
                maximumReadBytes: 1024 * 1024,
                maximumBufferSize: 64 * 1024));
        _nodes.Add(FilePath.Root, new MemoryNode(FileEntryKind.Directory, [], NextRevision()));
    }

    public FileProviderProfileId ProfileId { get; }

    public FileProviderCapabilities Capabilities { get; }

    public ValueTask<FileProviderResult<FilePage>> ListAsync(
        FileListRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            if (request.PageSize > Capabilities.Limits.MaximumListPageSize)
            {
                return Failure<FilePage>(FileProviderErrorCode.LimitExceeded, "The page is too large.");
            }

            var locationError = ValidateLocation(request.Location);
            if (locationError is not null)
            {
                return FileProviderResult<FilePage>.Failure(locationError);
            }

            var offsetResult = DecodeOffset(request.ContinuationToken);
            if (!offsetResult.IsSuccess)
            {
                return FileProviderResult<FilePage>.Failure(offsetResult.Error!);
            }

            lock (_gate)
            {
                if (!_nodes.TryGetValue(request.Location.Path, out var directory))
                {
                    return Failure<FilePage>(FileProviderErrorCode.NotFound, "The directory was not found.");
                }

                var versionError = CheckLocationVersion(request.Location, directory);
                if (versionError is not null)
                {
                    return FileProviderResult<FilePage>.Failure(versionError);
                }

                if (directory.Kind != FileEntryKind.Directory)
                {
                    return Failure<FilePage>(FileProviderErrorCode.NotDirectory, "The location is not a directory.");
                }

                var children = _nodes
                    .Where(pair => !pair.Key.Equals(request.Location.Path)
                        && pair.Key.Parent.Equals(request.Location.Path))
                    .OrderBy(pair => pair.Key.Name!.Value.Value, StringComparer.Ordinal)
                    .Skip(offsetResult.Value)
                    .Take(request.PageSize + 1)
                    .ToArray();
                var hasMore = children.Length > request.PageSize;
                var entries = children
                    .Take(request.PageSize)
                    .Select(pair => ToEntry(pair.Key, pair.Value))
                    .ToArray();
                FilePageToken? continuation = hasMore
                    ? EncodeOffset(offsetResult.Value + entries.Length)
                    : null;
                return FileProviderResult<FilePage>.Success(new FilePage(entries, continuation));
            }
        }, cancellationToken);

    public ValueTask<FileProviderResult<FileEntry>> StatAsync(
        FileStatRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            var locationError = ValidateLocation(request.Location);
            if (locationError is not null)
            {
                return FileProviderResult<FileEntry>.Failure(locationError);
            }

            lock (_gate)
            {
                if (!_nodes.TryGetValue(request.Location.Path, out var node))
                {
                    return Failure<FileEntry>(FileProviderErrorCode.NotFound, "The entry was not found.");
                }

                var versionError = CheckLocationVersion(request.Location, node);
                return versionError is null
                    ? FileProviderResult<FileEntry>.Success(ToEntry(request.Location.Path, node))
                    : FileProviderResult<FileEntry>.Failure(versionError);
            }
        }, cancellationToken);

    private FileProviderError? ValidateLocation(FileLocation location) =>
        location.ProviderProfileId == ProfileId
        && location.Authority == _authority
        && location.Address is FileLocationAddress.Hierarchical
            ? null
            : FileProviderError.Create(
                FileProviderErrorCode.InvalidLocation,
                "The location belongs to another provider or is not hierarchical.");

    private FileProviderError? CheckLocationVersion(FileLocation location, MemoryNode node) =>
        location.Version is null || location.Version == VersionOf(node)
            ? null
            : FileProviderError.Create(
                FileProviderErrorCode.PreconditionFailed,
                "The location version is stale.");

    private FileProviderError? CheckPrecondition(
        FileLocation location,
        FileMutationPrecondition precondition,
        MemoryNode? existing)
    {
        if (location.Version is { } locationVersion)
        {
            if (precondition is FileMutationPrecondition.Any)
            {
                precondition = new FileMutationPrecondition.VersionMatches(locationVersion);
            }
            else if (precondition is not FileMutationPrecondition.VersionMatches match
                     || match.Version != locationVersion)
            {
                return FileProviderError.Create(
                    FileProviderErrorCode.InvalidLocation,
                    "The version and mutation precondition disagree.");
            }
        }

        return precondition switch
        {
            FileMutationPrecondition.MustNotExist when existing is not null => FileProviderError.Create(
                FileProviderErrorCode.Conflict,
                "The destination already exists."),
            FileMutationPrecondition.MustExist when existing is null => FileProviderError.Create(
                FileProviderErrorCode.NotFound,
                "The destination does not exist."),
            FileMutationPrecondition.VersionMatches when existing is null => FileProviderError.Create(
                FileProviderErrorCode.PreconditionFailed,
                "The destination does not exist."),
            FileMutationPrecondition.VersionMatches match when VersionOf(existing!) != match.Version =>
                FileProviderError.Create(
                    FileProviderErrorCode.PreconditionFailed,
                    "The destination version is stale."),
            _ => null,
        };
    }

    private FileProviderError? ValidateParent(FilePath path)
    {
        if (!_nodes.TryGetValue(path.Parent, out var parent))
        {
            return FileProviderError.Create(FileProviderErrorCode.NotFound, "The parent directory was not found.");
        }

        return parent.Kind == FileEntryKind.Directory
            ? null
            : FileProviderError.Create(FileProviderErrorCode.NotDirectory, "The parent is not a directory.");
    }

    private FileEntry ToEntry(FilePath path, MemoryNode node)
    {
        var version = VersionOf(node);
        var location = new FileLocation(ProfileId, _authority, path, version);
        return new FileEntry(
            location,
            node.Kind,
            node.Kind == FileEntryKind.File ? node.Content.LongLength : null,
            DateTimeOffset.UnixEpoch.AddTicks(node.Revision),
            version,
            path.Name is { } name && name.Value.StartsWith('.'));
    }

    private void TouchParents(FilePath path)
    {
        var parent = path.Parent;
        while (_nodes.TryGetValue(parent, out var node))
        {
            node.Revision = NextRevision();
            if (parent.IsRoot)
            {
                return;
            }

            parent = parent.Parent;
        }
    }

    private long NextRevision() => ++_revision;

    private static FileVersion VersionOf(MemoryNode node) => new($"memory-{node.Revision}");

    private static FileProviderResult<T> Failure<T>(FileProviderErrorCode code, string message) =>
        FileProviderResult<T>.Failure(FileProviderError.Create(code, message));

    private static async ValueTask<FileProviderResult<T>> ExecuteAsync<T>(
        Func<FileProviderResult<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<T>(FileProviderErrorCode.Cancelled, "The file operation was cancelled.");
        }
    }

    private static FileProviderResult<int> DecodeOffset(FilePageToken? token)
    {
        if (token is null)
        {
            return FileProviderResult<int>.Success(0);
        }

        try
        {
            var bytes = Convert.FromBase64String(token.Value.Value);
            var offset = bytes.Length == sizeof(int) ? BitConverter.ToInt32(bytes) : -1;
            return offset >= 0
                ? FileProviderResult<int>.Success(offset)
                : Failure<int>(FileProviderErrorCode.InvalidLocation, "The continuation token is invalid.");
        }
        catch (FormatException)
        {
            return Failure<int>(FileProviderErrorCode.InvalidLocation, "The continuation token is invalid.");
        }
    }

    private static FilePageToken EncodeOffset(int offset) =>
        new(Convert.ToBase64String(BitConverter.GetBytes(offset)));

    private sealed class MemoryNode(FileEntryKind kind, byte[] content, long revision)
    {
        public FileEntryKind Kind { get; } = kind;

        public byte[] Content { get; set; } = content;

        public long Revision { get; set; } = revision;
    }
}
