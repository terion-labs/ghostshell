using GhostShell.Application;

namespace GhostShell.Files.Tests;

/// <summary>
/// Whole-file content for previews: local files open in place, remote files
/// arrive whole and are kept — in memory or in a cache container — never as
/// plain files on disk.
/// </summary>
public sealed class FilePanelClientContentSourceTests : IDisposable
{
    [Fact]
    public async Task An_understated_size_hint_spills_actual_bytes_without_truncation()
    {
        using var cache = new PreviewContentCache(KeepBetweenRuns(false), _cacheDirectory);
        using var pending = cache.BeginPut("understated", 1);
        var destination = pending.Destination;
        var bytes = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        await destination.WriteAsync(bytes.AsMemory(0, 1024));
        await destination.WriteAsync(bytes.AsMemory(1024));
        Assert.Same(destination, pending.Destination);
        Assert.NotEmpty(Directory.GetFiles(_cacheDirectory));
        using var content = pending.Commit();
        Assert.Equal(bytes, await content.ReadAllBytesAsync(CancellationToken.None));
    }

    private readonly string _root =
        Directory.CreateTempSubdirectory("ghostshell-content-source").FullName;

    private readonly string _cacheDirectory =
        Directory.CreateTempSubdirectory("ghostshell-content-cache").FullName;

    private readonly List<PreviewContentCache> _caches = [];

    public void Dispose()
    {
        foreach (var cache in _caches)
        {
            cache.Dispose();
        }

        foreach (var directory in new[] { _root, _cacheDirectory })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task A_local_file_is_read_in_place_without_a_copy()
    {
        var path = Path.Combine(_root, "database.db");
        await File.WriteAllTextAsync(path, "SQLite format 3\0payload");
        var client = CreateLocalClient();

        var result = await ((IFileContentSource)client).OpenContentAsync(
            Location("database.db"),
            maximumBytes: 1024,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        using var content = result.Value!;
        Assert.Equal(path, content.LocalPath);
        await using var stream = content.OpenRead();
        Assert.Equal(
            await File.ReadAllBytesAsync(path),
            await ReadAllAsync(stream));
    }

    [Fact]
    public async Task A_large_local_file_still_opens_because_nothing_is_copied()
    {
        // The ceiling bounds downloading, not opening: a database already on
        // this machine is opened where it lies. Refusing it by size would be a
        // limit invented for its own sake.
        var path = Path.Combine(_root, "big.db");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        var client = CreateLocalClient();

        var result = await ((IFileContentSource)client).OpenContentAsync(
            Location("big.db"),
            maximumBytes: 1024,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(path, result.Value!.LocalPath);
    }

    [Fact]
    public async Task A_remote_file_over_the_ceiling_is_refused_rather_than_truncated()
    {
        var provider = new RemoteBytesProvider(new byte[4096]);
        var client = ClientFor(provider);

        var result = await ((IFileContentSource)client).OpenContentAsync(
            RemoteLocation(provider, "big.db"),
            maximumBytes: 1024,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FilePanelErrorCode.LimitExceeded, result.Error!.Code);
    }

    [Fact]
    public async Task A_remote_file_arrives_whole_even_through_bounded_reads()
    {
        // The provider caps each read at 1 KiB, so a single read would have
        // produced a truncated — corrupt — file.
        var content = new byte[2500];
        Random.Shared.NextBytes(content);
        var provider = new RemoteBytesProvider(content);
        var client = ClientFor(provider);

        var result = await ((IFileContentSource)client).OpenContentAsync(
            RemoteLocation(provider, "remote.db"),
            maximumBytes: 1024 * 1024,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        using var opened = result.Value!;
        Assert.Null(opened.LocalPath);
        Assert.Equal(content, await opened.ReadAllBytesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_file_already_fetched_is_served_without_asking_the_provider_again()
    {
        var provider = new RemoteBytesProvider(new byte[1500]);
        var client = ClientFor(provider);
        var location = RemoteLocation(provider, "cached.db");

        var first = await ((IFileContentSource)client).OpenContentAsync(
            location,
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(first.IsSuccess);
        var reads = provider.ReadCount;

        var second = await ((IFileContentSource)client).OpenContentAsync(
            location,
            maximumBytes: 1024 * 1024,
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(reads, provider.ReadCount);
        Assert.Equal(
            await first.Value!.ReadAllBytesAsync(CancellationToken.None),
            await second.Value!.ReadAllBytesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_large_fetch_is_served_from_its_container_the_second_time()
    {
        // Above the threshold the bytes live in a container on disk, and a
        // second selection must be a container hit, not a download.
        var content = new byte[200 * 1024];
        Random.Shared.NextBytes(content);
        var provider = new RemoteBytesProvider(content);
        var client = ClientFor(provider, KeepBetweenRuns(true));
        var location = RemoteLocation(provider, "big.bin");

        var first = await ((IFileContentSource)client).OpenContentAsync(
            location,
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(first.IsSuccess);
        var reads = provider.ReadCount;

        var second = await ((IFileContentSource)client).OpenContentAsync(
            location,
            maximumBytes: 1024 * 1024,
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(reads, provider.ReadCount);
        Assert.Equal(content, await second.Value!.ReadAllBytesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_changed_file_is_downloaded_again_rather_than_served_stale()
    {
        var provider = new RemoteBytesProvider(new byte[1500]);
        var client = ClientFor(provider);
        var location = RemoteLocation(provider, "changing.db");

        var first = await ((IFileContentSource)client).OpenContentAsync(
            location,
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(first.IsSuccess);

        // A new version of the same path: different content, different identity.
        var replacement = new byte[2600];
        Random.Shared.NextBytes(replacement);
        provider.Replace(replacement, "version-2");
        var second = await ((IFileContentSource)client).OpenContentAsync(
            location,
            maximumBytes: 1024 * 1024,
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(
            replacement,
            await second.Value!.ReadAllBytesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Nothing_a_remote_provider_served_is_on_disk_in_the_clear()
    {
        // The central promise of the preview cache. With persistence off, a
        // large remote file lives only in the encrypted session container:
        // every file the cache writes may be searched for the payload and none
        // may contain it readably.
        var content = new byte[300 * 1024];
        Random.Shared.NextBytes(content);
        var provider = new RemoteBytesProvider(content);
        var client = ClientFor(provider, KeepBetweenRuns(false));

        var result = await ((IFileContentSource)client).OpenContentAsync(
            RemoteLocation(provider, "secret.bin"),
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(content, await result.Value!.ReadAllBytesAsync(CancellationToken.None));

        var probe = content.AsMemory(0, 64);
        // The lock file is held exclusively by design and holds nothing.
        var written = Directory.GetFiles(_cacheDirectory, "*", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(".lock", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(written);
        foreach (var file in written)
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.True(
                bytes.AsSpan().IndexOf(probe.Span) < 0,
                $"Remote file content was found in the clear in {Path.GetFileName(file)}.");
        }
    }

    [Fact]
    public async Task Closing_the_session_removes_its_container_from_disk()
    {
        var content = new byte[300 * 1024];
        Random.Shared.NextBytes(content);
        var provider = new RemoteBytesProvider(content);
        var cache = NewCache(KeepBetweenRuns(false));
        var client = ClientFor(provider, cache: cache);

        var result = await ((IFileContentSource)client).OpenContentAsync(
            RemoteLocation(provider, "ephemeral.bin"),
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.NotEmpty(Directory.GetFiles(_cacheDirectory, "session-*.db"));

        cache.Dispose();

        Assert.Empty(Directory.GetFiles(_cacheDirectory, "session-*"));
    }

    [Fact]
    public async Task A_dead_sessions_leavings_are_swept_and_a_live_ones_are_not()
    {
        // A dead session: container and lock exist, nobody holds the lock.
        var deadId = new string('d', 32);
        var deadDb = Path.Combine(_cacheDirectory, $"session-{deadId}.db");
        var deadLock = Path.Combine(_cacheDirectory, $"session-{deadId}.lock");
        await File.WriteAllBytesAsync(deadDb, new byte[64]);
        await File.WriteAllBytesAsync(deadLock, []);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(deadDb, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(deadLock, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // A live session: another cache in the same directory, holding its
        // lock because it has stored something.
        var living = NewCache(KeepBetweenRuns(false));
        var provider = new RemoteBytesProvider(RandomBytes(200 * 1024));
        var client = ClientFor(provider, cache: living);
        var stored = await ((IFileContentSource)client).OpenContentAsync(
            RemoteLocation(provider, "alive.bin"),
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(stored.IsSuccess);
        var livingContainer = Assert.Single(
            Directory.GetFiles(_cacheDirectory, "session-*.db"),
            file => !file.EndsWith($"session-{deadId}.db", StringComparison.Ordinal)
                // LiteDB's write-ahead log sits beside the container and
                // matches the same pattern.
                && !file.EndsWith("-log.db", StringComparison.Ordinal));

        // A newcomer sweeps: the dead session goes, the living one stays.
        NewCache(KeepBetweenRuns(false));

        Assert.False(File.Exists(deadDb), "The dead session's container survived the sweep.");
        Assert.False(File.Exists(deadLock), "The dead session's lock survived the sweep.");
        Assert.True(File.Exists(livingContainer), "A live session's container was swept away.");
    }

    [Fact]
    public async Task Turning_persistence_off_removes_the_persistent_container()
    {
        var preferences = KeepBetweenRuns(true);
        var provider = new RemoteBytesProvider(RandomBytes(200 * 1024));
        var client = ClientFor(provider, preferences);
        var stored = await ((IFileContentSource)client).OpenContentAsync(
            RemoteLocation(provider, "kept.bin"),
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(stored.IsSuccess);
        Assert.True(File.Exists(Path.Combine(_cacheDirectory, "store.db")));

        await preferences.ApplyAsync(
            preferences.Current with { KeepPreviewsBetweenRuns = false },
            CancellationToken.None);

        // The promise changed, so what was written under the old one is gone
        // now — not at some future startup.
        Assert.False(File.Exists(Path.Combine(_cacheDirectory, "store.db")));
    }

    [Fact]
    public async Task With_encryption_on_even_the_persistent_container_holds_nothing_in_the_clear()
    {
        var content = RandomBytes(300 * 1024);
        var provider = new RemoteBytesProvider(content);
        var encryption = new TestApplicationEncryption { IsEnabled = true };
        var cache = new PreviewContentCache(KeepBetweenRuns(true), _cacheDirectory, encryption);
        _caches.Add(cache);
        var client = ClientFor(provider, cache: cache);

        var stored = await ((IFileContentSource)client).OpenContentAsync(
            RemoteLocation(provider, "kept-secret.bin"),
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(stored.IsSuccess);
        Assert.True(File.Exists(Path.Combine(_cacheDirectory, "store.db")));

        var probe = content.AsMemory(0, 64);
        foreach (var file in Directory.GetFiles(_cacheDirectory)
                     .Where(file => !file.EndsWith(".lock", StringComparison.Ordinal)))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.True(
                bytes.AsSpan().IndexOf(probe.Span) < 0,
                $"Remote file content was found in the clear in {Path.GetFileName(file)}.");
        }
    }

    [Fact]
    public async Task Toggling_encryption_drops_the_persistent_container()
    {
        var provider = new RemoteBytesProvider(RandomBytes(200 * 1024));
        // Disabled means no password, exactly as the runtime behaves.
        var encryption = new TestApplicationEncryption
        {
            IsEnabled = false,
            PersistentCachePassword = null,
        };
        var cache = new PreviewContentCache(KeepBetweenRuns(true), _cacheDirectory, encryption);
        _caches.Add(cache);
        var client = ClientFor(provider, cache: cache);
        var stored = await ((IFileContentSource)client).OpenContentAsync(
            RemoteLocation(provider, "soon-stale.bin"),
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(stored.IsSuccess);
        Assert.True(File.Exists(Path.Combine(_cacheDirectory, "store.db")));

        // Turning encryption on: what the container holds is exactly what
        // must stop existing in the clear.
        encryption.IsEnabled = true;
        encryption.PersistentCachePassword = "0011223344";
        encryption.RaiseChanged();

        Assert.False(File.Exists(Path.Combine(_cacheDirectory, "store.db")));
    }

    [Fact]
    public void Cache_root_and_generated_files_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(_root, "owner-only-cache");
        using var cache = new PreviewContentCache(KeepBetweenRuns(false), directory);
        using var pending = cache.BeginPut("secure-entry", 128 * 1024);
        pending.Destination.Write(new byte[128 * 1024]);
        using var content = pending.Commit();

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(directory));
        foreach (var path in Directory.GetFiles(directory))
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void Permissive_unix_cache_root_is_rejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(_root, "permissive-cache");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute);

        Assert.Throws<UnauthorizedAccessException>(() =>
            new PreviewContentCache(KeepBetweenRuns(false), directory));

        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Linked_persistent_entry_is_rejected_without_touching_its_target()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(_root, "linked-entry-cache");
        Directory.CreateDirectory(directory, PrivateContentPathGuard.OwnerDirectoryMode);
        var target = Path.Combine(_root, "link-target.txt");
        await File.WriteAllTextAsync(target, "must survive");
        File.SetUnixFileMode(target, PrivateContentPathGuard.OwnerFileMode);
        File.CreateSymbolicLink(Path.Combine(directory, "store.db"), target);

        Assert.Throws<InvalidDataException>(() =>
            new PreviewContentCache(KeepBetweenRuns(true), directory));
        Assert.Equal("must survive", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public void Linked_cache_root_is_rejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(_root, "real-cache");
        Directory.CreateDirectory(target, PrivateContentPathGuard.OwnerDirectoryMode);
        var linked = Path.Combine(_root, "linked-cache");
        Directory.CreateSymbolicLink(linked, target);

        Assert.Throws<InvalidDataException>(() =>
            new PreviewContentCache(KeepBetweenRuns(false), linked));
    }

    private sealed class TestApplicationEncryption : IApplicationEncryption
    {
        public bool IsSupported => true;

        public bool IsEnabled { get; set; }

        public bool AwaitingUnlock => false;

        public string? UnsupportedReason => null;

        public string? PersistentCachePassword { get; set; } =
            "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public ValueTask<string?> SetEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task An_unknown_profile_fails_without_touching_the_filesystem()
    {
        var client = CreateLocalClient();

        var result = await ((IFileContentSource)client).OpenContentAsync(
            new FilePanelLocation(
                "absent-profile",
                "fixture",
                new FilePanelAddress.Hierarchical(
                    FilePanelPath.FromSegments([new FilePanelPathSegment("database.db")]))),
            maximumBytes: 1024,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FilePanelErrorCode.UnknownProfile, result.Error!.Code);
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Preferences with the smallest allowed threshold, so modest fixtures are
    /// already "large" and exercise the container tier.
    /// </summary>
    private static TestFilePreviewPreferences KeepBetweenRuns(bool keep) => new(
        new FilePreviewSettings(
            AutoLoadThresholdBytes: 64 * 1024,
            KeepPreviewsBetweenRuns: keep,
            CacheBudgetBytes: 64L * 1024 * 1024));

    private PreviewContentCache NewCache(TestFilePreviewPreferences preferences)
    {
        var cache = new PreviewContentCache(preferences, _cacheDirectory);
        _caches.Add(cache);
        return cache;
    }

    private FilePanelClient CreateLocalClient()
    {
        var provider = LocalFileProvider.CreateForCurrentPlatform(
            new LocalFileProviderOptions(
                new FileProviderProfileId("local-test"),
                new FileAuthority("fixture"),
                _root));
        var registration = new FileProviderRegistration(
            "Local files",
            OperatingSystem.IsWindows() ? FileProviderFamily.Windows : FileProviderFamily.Posix,
            provider,
            new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root));
        return new FilePanelClient([registration]);
    }

    private static FilePanelLocation Location(string name) =>
        new(
            "local-test",
            "fixture",
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments([new FilePanelPathSegment(name)])));

    private FilePanelClient ClientFor(
        RemoteBytesProvider provider,
        TestFilePreviewPreferences? preferences = null,
        PreviewContentCache? cache = null) =>
        new(
            [
                new FileProviderRegistration(
                    "Remote files",
                    FileProviderFamily.Sftp,
                    provider,
                    provider.Root),
            ],
            TimeProvider.System,
            cache ?? NewCache(preferences ?? KeepBetweenRuns(true)));

    private static FilePanelLocation RemoteLocation(RemoteBytesProvider provider, string name) =>
        new(
            provider.ProfileId.Value,
            "fixture",
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments([new FilePanelPathSegment(name)])));

    /// <summary>
    /// A provider with no local path, whose bounded reads are small enough
    /// that a whole-file fetch must loop. Everything content needs and
    /// nothing it does not.
    /// </summary>
    private sealed class RemoteBytesProvider(byte[] content) : IFileProvider
    {
        private static readonly FileAuthority Authority = new("fixture");

        private byte[] _content = content;
        private string _version = "fixture-version";

        public int ReadCount { get; private set; }

        public void Replace(byte[] replacement, string version)
        {
            _content = replacement;
            _version = version;
        }

        public FileProviderProfileId ProfileId { get; } = new("remote-content-test");

        public FileLocation Root => new(ProfileId, Authority, FilePath.Root);

        public FileProviderCapabilities Capabilities { get; } = new(
            FileProviderCapability.Stat | FileProviderCapability.RangedRead,
            FileNameComparison.CaseSensitive,
            new FileProviderLimits(
                maximumListPageSize: 100,
                maximumReadBytes: 1024,
                maximumBufferSize: 1024));

        public ValueTask<FileProviderResult<FilePage>> ListAsync(
            FileListRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileEntry>> StatAsync(
            FileStatRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(FileProviderResult<FileEntry>.Success(new FileEntry(
                request.Location,
                FileEntryKind.File,
                _content.Length,
                LastModifiedAt: null,
                new FileVersion(_version),
                IsHidden: false)));

        public async ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
            FileReadRequest request,
            Stream destination,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            var offset = checked((int)request.Offset);
            var count = (int)Math.Min(request.MaximumBytes, Math.Max(0, _content.Length - offset));
            if (count > 0)
            {
                await destination.WriteAsync(_content.AsMemory(offset, count), cancellationToken);
            }

            return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
                request.Location,
                request.Offset,
                count,
                offset + count < _content.Length));
        }

        public ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
            FileWriteRequest request,
            Stream source,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
            FileCreateDirectoryRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileEntry>> RenameAsync(
            FileRenameRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
            FileTransferRequest request,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
            FileDeleteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

/// <summary>
/// In-memory preferences for exercising the cache's policy edges without a
/// settings store.
/// </summary>
internal sealed class TestFilePreviewPreferences(FilePreviewSettings initial)
    : IFilePreviewPreferences
{
    public FilePreviewSettings Current { get; private set; } = initial;

    public event EventHandler? Changed;

    public ValueTask ApplyAsync(
        FilePreviewSettings settings,
        CancellationToken cancellationToken)
    {
        Current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }
}
