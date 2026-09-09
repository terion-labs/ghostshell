namespace Asura.Files.Tests;

internal sealed class FakeRemoteSessionFactory : IRemoteHierarchicalFileSessionFactory, IFtpFeatureSource
{
    private readonly FakeRemoteFileSystem _fileSystem = new();
    private int _failOpenCount;
    private int _cancelOpenCount;

    public FtpConnectionSnapshot? LastConnection { get; set; }

    public bool ReportUnknownFileSizes { get; set; }

    public bool StatDetectsAnyLinkInPath { get; set; }

    public List<string> StatPaths { get; } = [];

    public Func<CancellationToken, IReadOnlyList<RemoteFileEntry>>? ListingOverride { get; set; }

    public RemoteFileSessionErrorCode? OpenError { get; set; }

    public int OpenCount { get; private set; }

    public int FailOpenCount
    {
        get => _failOpenCount;
        set => _failOpenCount = value;
    }

    public int CancelOpenCount
    {
        get => _cancelOpenCount;
        set => _cancelOpenCount = value;
    }

    public void SeedLink(string path) => _fileSystem.AddLink(path);

    public void ReplaceFileWithLink(string path)
    {
        _fileSystem.DeleteFile(path, CancellationToken.None);
        _fileSystem.AddLink(path);
    }

    public void SeedDirectory(string path) =>
        _fileSystem.CreateDirectory(path, CancellationToken.None);

    public ValueTask<IRemoteHierarchicalFileSession> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenCount++;
        if (OpenError is { } openError)
        {
            throw new RemoteFileSessionException(openError, "The fake remote session was rejected.");
        }

        if (_cancelOpenCount > 0)
        {
            _cancelOpenCount--;
            throw new OperationCanceledException("The fake connection prompt was cancelled.");
        }

        if (_failOpenCount > 0)
        {
            _failOpenCount--;
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.Transient,
                "The fake remote endpoint is temporarily unavailable.",
                retryable: true);
        }

        return ValueTask.FromResult<IRemoteHierarchicalFileSession>(
            new FakeRemoteSession(
                _fileSystem,
                () => ReportUnknownFileSizes,
                token => ListingOverride?.Invoke(token),
                () => StatDetectsAnyLinkInPath,
                path => StatPaths.Add(path)));
    }
}

internal sealed class FakeRemoteSession(
    FakeRemoteFileSystem fileSystem,
    Func<bool> reportUnknownFileSizes,
    Func<CancellationToken, IReadOnlyList<RemoteFileEntry>?> listingOverride,
    Func<bool> statDetectsAnyLinkInPath,
    Action<string> recordStat) :
    IRemoteHierarchicalFileSession
{
    public bool StatDetectsAnyLinkInPath => statDetectsAnyLinkInPath();

    public ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (listingOverride(cancellationToken) is { } overridden)
        {
            return ValueTask.FromResult(overridden);
        }

        var entries = fileSystem.List(path, cancellationToken);
        return ValueTask.FromResult<IReadOnlyList<RemoteFileEntry>>(
            reportUnknownFileSizes()
                ? [.. entries.Select(WithoutKnownSize)]
                : entries);
    }

    public ValueTask<RemoteFileEntry?> StatAsync(
        string path,
        CancellationToken cancellationToken)
    {
        recordStat(path);
        var entry = fileSystem.Stat(path, cancellationToken);
        return ValueTask.FromResult(
            reportUnknownFileSizes() && entry is not null
                ? WithoutKnownSize(entry)
                : entry);
    }

    public ValueTask<Stream> OpenReadAsync(
        string path,
        long offset,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(fileSystem.OpenRead(path, offset, cancellationToken));

    public ValueTask<Stream> OpenCreateNewAsync(
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(fileSystem.OpenCreateNew(path, cancellationToken));

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        fileSystem.CreateDirectory(path, cancellationToken);
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        fileSystem.Rename(sourcePath, destinationPath, cancellationToken);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        fileSystem.DeleteFile(path, cancellationToken);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        fileSystem.DeleteDirectory(path, cancellationToken);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int?> GetPermissionsAsync(
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(fileSystem.GetPermissions(path, cancellationToken));

    public ValueTask SetPermissionsAsync(
        string path,
        int mode,
        CancellationToken cancellationToken)
    {
        fileSystem.SetPermissions(path, mode, cancellationToken);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static RemoteFileEntry WithoutKnownSize(RemoteFileEntry entry) =>
        entry.Kind == FileEntryKind.File ? entry with { Size = null } : entry;
}

internal sealed class FakeRemoteFileSystem
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal)
    {
        ["/"] = new Node(FileEntryKind.Directory, [], revision: 1),
    };
    private long _revision = 1;

    public IReadOnlyList<RemoteFileEntry> List(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var directory = Find(path);
            if (directory.Kind != FileEntryKind.Directory)
            {
                throw Error(RemoteFileSessionErrorCode.NotDirectory, "The fake path is not a directory.");
            }

            return [.. _nodes
                .Where(pair => !string.Equals(pair.Key, path, StringComparison.Ordinal) && string.Equals(Parent(pair.Key), path, StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Entry(Name(pair.Key), pair.Value))];
        }
    }

    public int? GetPermissions(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return _nodes.TryGetValue(path, out var node) ? node.Mode : null;
        }
    }

    public void SetPermissions(string path, int mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Find(path).Mode = mode & 0x1FF;
        }
    }

    public RemoteFileEntry? Stat(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return _nodes.TryGetValue(path, out var node) ? Entry(Name(path), node) : null;
        }
    }

    public Stream OpenRead(string path, long offset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var node = Find(path);
            if (node.Kind == FileEntryKind.Directory)
            {
                throw Error(RemoteFileSessionErrorCode.IsDirectory, "The fake path is a directory.");
            }

            var stream = new MemoryStream(node.Content, writable: false)
            {
                Position = offset
            };
            return stream;
        }
    }

    public Stream OpenCreateNew(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_nodes.ContainsKey(path))
            {
                throw Error(RemoteFileSessionErrorCode.AlreadyExists, "The fake path already exists.");
            }

            RequireDirectory(Parent(path));
            return new CommitStream(content => CommitFile(path, content));
        }
    }

    public void CreateDirectory(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_nodes.ContainsKey(path))
            {
                throw Error(RemoteFileSessionErrorCode.AlreadyExists, "The fake path already exists.");
            }

            RequireDirectory(Parent(path));
            _nodes.Add(path, new Node(FileEntryKind.Directory, [], NextRevision()));
            Touch(Parent(path));
        }
    }

    public void AddLink(string path)
    {
        lock (_gate)
        {
            if (_nodes.ContainsKey(path))
            {
                throw new InvalidOperationException("The fake path already exists.");
            }

            RequireDirectory(Parent(path));
            _nodes.Add(path, new Node(FileEntryKind.Link, [], NextRevision()));
            Touch(Parent(path));
        }
    }

    public void Rename(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var source = Find(sourcePath);
            if (_nodes.ContainsKey(destinationPath))
            {
                throw Error(RemoteFileSessionErrorCode.AlreadyExists, "The fake destination exists.");
            }

            RequireDirectory(Parent(destinationPath));
            var moving = _nodes
                .Where(pair => string.Equals(pair.Key, sourcePath, StringComparison.Ordinal) || IsDescendant(pair.Key, sourcePath))
                .ToArray();
            foreach (var pair in moving)
            {
                _nodes.Remove(pair.Key);
            }

            foreach (var pair in moving)
            {
                var suffix = pair.Key[sourcePath.Length..];
                pair.Value.Revision = NextRevision();
                _nodes.Add(destinationPath + suffix, pair.Value);
            }

            source.Revision = NextRevision();
            Touch(Parent(sourcePath));
            Touch(Parent(destinationPath));
        }
    }

    public void DeleteFile(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var node = Find(path);
            if (node.Kind == FileEntryKind.Directory)
            {
                throw Error(RemoteFileSessionErrorCode.IsDirectory, "The fake path is a directory.");
            }

            _nodes.Remove(path);
            Touch(Parent(path));
        }
    }

    public void DeleteDirectory(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var node = Find(path);
            if (node.Kind != FileEntryKind.Directory)
            {
                throw Error(RemoteFileSessionErrorCode.NotDirectory, "The fake path is not a directory.");
            }

            if (_nodes.Keys.Any(candidate => IsDescendant(candidate, path)))
            {
                throw Error(RemoteFileSessionErrorCode.DirectoryNotEmpty, "The fake directory is not empty.");
            }

            _nodes.Remove(path);
            Touch(Parent(path));
        }
    }

    private void CommitFile(string path, byte[] content)
    {
        lock (_gate)
        {
            if (_nodes.ContainsKey(path))
            {
                throw Error(RemoteFileSessionErrorCode.AlreadyExists, "The fake path already exists.");
            }

            _nodes.Add(path, new Node(FileEntryKind.File, content, NextRevision()));
            Touch(Parent(path));
        }
    }

    private Node Find(string path) => _nodes.TryGetValue(path, out var node)
        ? node
        : throw Error(RemoteFileSessionErrorCode.NotFound, "The fake path was not found.");

    private void RequireDirectory(string path)
    {
        var parent = Find(path);
        if (parent.Kind != FileEntryKind.Directory)
        {
            throw Error(RemoteFileSessionErrorCode.NotDirectory, "The fake parent is not a directory.");
        }
    }

    private void Touch(string path)
    {
        if (_nodes.TryGetValue(path, out var node))
        {
            node.Revision = NextRevision();
        }
    }

    private long NextRevision() => ++_revision;

    private static RemoteFileEntry Entry(string name, Node node) => new(
        name,
        node.Kind,
        node.Kind == FileEntryKind.File ? node.Content.LongLength : null,
        DateTimeOffset.UnixEpoch.AddTicks(node.Revision),
        $"fake-{node.Revision}",
        new RemotePosixMetadata(1000, 1000, node.Kind == FileEntryKind.Directory ? 0x1ED : 0x1A4));

    private static string Parent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string Name(string path) => string.Equals(path, "/", StringComparison.Ordinal) ? string.Empty : path[(path.LastIndexOf('/') + 1)..];

    private static bool IsDescendant(string candidate, string ancestor) =>
        candidate.Length > ancestor.Length
        && candidate.StartsWith(string.Equals(ancestor, "/", StringComparison.Ordinal) ? "/" : $"{ancestor}/", StringComparison.Ordinal);

    private static RemoteFileSessionException Error(RemoteFileSessionErrorCode code, string message) =>
        new(code, message);

    private sealed class Node(FileEntryKind kind, byte[] content, long revision)
    {
        public FileEntryKind Kind { get; } = kind;

        public byte[] Content { get; } = content;

        public long Revision { get; set; } = revision;

        /// <summary>
        /// The nine bits, which SFTP carries in the same attribute block as the
        /// size. A fake that had none would let the provider claim a capability
        /// nothing in the suite could exercise.
        /// </summary>
        public int Mode { get; set; } = kind == FileEntryKind.Directory
            ? 0b111_101_101
            : 0b110_100_100;
    }

    private sealed class CommitStream(Action<byte[]> commit) : MemoryStream
    {
        private bool _committed;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Commit();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Commit();
            Dispose(disposing: false);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        private void Commit()
        {
            if (_committed)
            {
                return;
            }

            _committed = true;
            commit(ToArray());
        }
    }
}
