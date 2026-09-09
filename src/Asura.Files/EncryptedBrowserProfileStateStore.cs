using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;
using LiteDB;

namespace Asura.Files;

/// <summary>
/// Stores complete Chromium request-context directories as encrypted LiteDB
/// blobs. A unique blob is written first; only a complete archive replaces the
/// manifest pointer, so a crash cannot turn the previous good state into a
/// partially written profile.
/// </summary>
public sealed class EncryptedBrowserProfileStateStore :
    IBrowserProfileStateStore,
    IDisposable
{
    private const string DatabaseFileName = "browser-profiles.db";
    private const string ManifestCollection = "browser_profile_state";
    private const int ArchiveSchemaVersion = 1;
    private const int MaximumArchiveEntries = 100_000;
    private const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly IApplicationEncryption _encryption;
    private bool _disposed;

    public EncryptedBrowserProfileStateStore(
        string directory,
        IApplicationEncryption encryption)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _encryption = encryption ?? throw new ArgumentNullException(nameof(encryption));
        PrivateContentPathGuard.EnsurePrivateDirectory(_directory);
        ValidateOwnedEntries();
        _encryption.Changed += OnEncryptionChanged;
    }

    public bool IsRetentionEnabled => _encryption.IsEnabled;

    public bool IsAvailable =>
        IsRetentionEnabled
        && _encryption.PersistentCachePassword is not null;

    public string? UnavailableReason => IsAvailable
        ? null
        : "Durable browser sessions require application encryption and its operating-system protected key.";

    public BrowserProfileStoredState Inspect(BrowserProfileSelection selection)
    {
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!File.Exists(DatabasePath) || !IsAvailable)
                {
                    return new BrowserProfileStoredState(false, 0);
                }

                using var database = OpenDatabase(create: false);
                var manifests = FindManifests(database, selection);
                return manifests.Count == 0
                    ? new BrowserProfileStoredState(false, 0)
                    : new BrowserProfileStoredState(
                        true,
                        manifests.Sum(manifest => manifest["contentBytes"].AsInt64));
            }
        }
        catch (LiteException exception)
        {
            throw InvalidContainer(exception);
        }
    }

    public IReadOnlyList<BrowserProfileStateKey> ListKeys(
        BrowserProfileSelection selection)
    {
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!File.Exists(DatabasePath) || !IsAvailable)
                {
                    return [];
                }

                using var database = OpenDatabase(create: false);
                return
                [
                    .. FindManifests(database, selection)
                    .Select(document => new BrowserProfileStateKey(
                        new BrowserProfileSelection(
                            new Asura.Core.BrowserProfileId(
                                document["profileId"].AsString),
                            new BrowserProfileKey(
                                (BrowserProfileKind)document["partitionKind"].AsInt32,
                                document["partitionIdentity"].AsString)),
                        document["route"].AsString)),
                ];
            }
        }
        catch (LiteException exception)
        {
            throw InvalidContainer(exception);
        }
    }

    public void Restore(BrowserProfileStateKey key, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RequireAvailable();
                PrepareEmptyDestination(destinationDirectory);
                if (!File.Exists(DatabasePath))
                {
                    return;
                }

                try
                {
                    using var database = OpenDatabase(create: false);
                    var manifest = FindManifest(database, StorageId(key));
                    if (manifest is null)
                    {
                        return;
                    }

                    var blobId = manifest["blobId"].AsString;
                    var stored = database.GetStorage<string>().FindById(blobId)
                        ?? throw new InvalidDataException(
                            "The encrypted browser profile archive is incomplete.");
                    using var source = database.GetStorage<string>().OpenRead(blobId);
                    RestoreArchive(source, destinationDirectory);
                }
                catch
                {
                    DeleteOwnedTreeContents(destinationDirectory);
                    throw;
                }
            }
        }
        catch (LiteException exception)
        {
            throw InvalidContainer(exception);
        }
    }

    public long Seal(BrowserProfileStateKey key, string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var stage = "validate";
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RequireAvailable();
                ValidateSourceRoot(sourceDirectory);

                stage = "open-container";
                using var database = OpenDatabase(create: true);
                var storageId = StorageId(key);
                var blobId = $"browser-state/{storageId}/{Guid.NewGuid():n}";
                var storage = database.GetStorage<string>();
                long contentBytes;
                try
                {
                    stage = "open-archive";
                    using (var destination = storage.OpenWrite(blobId, blobId))
                    using (var archive = new ZipArchive(
                               destination,
                               ZipArchiveMode.Create,
                               leaveOpen: false,
                               entryNameEncoding: Encoding.UTF8))
                    {
                        contentBytes = WriteArchive(archive, sourceDirectory, ref stage);
                        stage = "close-archive";
                    }

                    stage = "write-metadata";
                    storage.SetMetadata(blobId, new BsonDocument
                    {
                        ["schema"] = ArchiveSchemaVersion,
                        ["contentBytes"] = contentBytes,
                        ["complete"] = true,
                    });

                    stage = "write-manifest";
                    var manifests = database.GetCollection<BsonDocument>(ManifestCollection);
                    var previous = manifests.FindById(storageId);
                    manifests.Upsert(new BsonDocument
                    {
                        ["_id"] = storageId,
                        ["blobId"] = blobId,
                        ["contentBytes"] = contentBytes,
                        ["schema"] = ArchiveSchemaVersion,
                        ["profileId"] = key.Selection.ProfileId.Value,
                        ["partitionKind"] = (int)key.Selection.Partition.Kind,
                        ["partitionIdentity"] = key.Selection.Partition.Identity,
                        ["route"] = key.Route,
                    });
                    if (previous is not null)
                    {
                        stage = "delete-previous";
                        storage.Delete(previous["blobId"].AsString);
                    }

                    stage = "remove-unused";
                    RemoveUnreferencedBlobs(database);
                    stage = "harden-container";
                    HardenGeneratedFiles();
                    stage = "close-container";
                    return contentBytes;
                }
                catch
                {
                    storage.Delete(blobId);
                    throw;
                }
            }
        }
        catch (LiteException exception)
        {
            throw InvalidContainer(exception);
        }
        catch (IOException exception)
        {
            exception.Data["Asura.BrowserSealStage"] = stage;
            throw;
        }
    }

    public long Delete(BrowserProfileSelection selection)
    {
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RequireAvailable();
                if (!File.Exists(DatabasePath))
                {
                    return 0;
                }

                using var database = OpenDatabase(create: false);
                var manifests = database.GetCollection<BsonDocument>(ManifestCollection);
                var matching = FindManifests(database, selection);
                if (matching.Count == 0)
                {
                    return 0;
                }

                var bytes = matching.Sum(manifest => manifest["contentBytes"].AsInt64);
                var storage = database.GetStorage<string>();
                foreach (var manifest in matching)
                {
                    manifests.Delete(manifest["_id"]);
                    storage.Delete(manifest["blobId"].AsString);
                }

                return bytes;
            }
        }
        catch (LiteException exception)
        {
            throw InvalidContainer(exception);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _encryption.Changed -= OnEncryptionChanged;
        }
    }

    private string DatabasePath => Path.Combine(_directory, DatabaseFileName);

    private string LogPath => Path.Combine(_directory, "browser-profiles-log.db");

    private LiteDatabase OpenDatabase(bool create)
    {
        if (!create && !File.Exists(DatabasePath))
        {
            throw new FileNotFoundException(
                "The encrypted browser profile store does not exist.",
                DatabasePath);
        }

        PrivateContentPathGuard.EnsurePrivateFile(DatabasePath);
        PrivateContentPathGuard.ValidateOptionalPrivateFile(LogPath);
        var database = new LiteDatabase(new ConnectionString
        {
            Filename = DatabasePath,
            Password = _encryption.PersistentCachePassword,
            Connection = ConnectionType.Direct,
        });
        HardenGeneratedFiles();
        return database;
    }

    private void RequireAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(UnavailableReason);
        }
    }

    private void OnEncryptionChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        lock (_gate)
        {
            if (_disposed || IsRetentionEnabled)
            {
                return;
            }

            DeleteContainer();
        }
    }

    private void DeleteContainer()
    {
        DeletePrivateFile(DatabasePath);
        DeletePrivateFile(LogPath);
    }

    private void ValidateOwnedEntries()
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(_directory))
        {
            var name = Path.GetFileName(entry);
            if (!string.Equals(name, DatabaseFileName, StringComparison.Ordinal)
                && !string.Equals(name, "browser-profiles-log.db", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The encrypted browser profile directory contains an unexpected entry.");
            }

            PrivateContentPathGuard.ValidatePrivateFile(entry);
        }
    }

    private void HardenGeneratedFiles()
    {
        PrivateContentPathGuard.HardenGeneratedFile(DatabasePath);
        PrivateContentPathGuard.HardenGeneratedFile(LogPath);
    }

    private static BsonDocument? FindManifest(LiteDatabase database, string storageId)
    {
        var document = database
            .GetCollection<BsonDocument>(ManifestCollection)
            .FindById(storageId);
        if (document is null)
        {
            return null;
        }

        if (document["schema"].AsInt32 != ArchiveSchemaVersion
            || document["contentBytes"].AsInt64 < 0
            || string.IsNullOrWhiteSpace(document["blobId"].AsString))
        {
            throw new InvalidDataException(
                "The encrypted browser profile manifest is invalid.");
        }

        return document;
    }

    private static IReadOnlyList<BsonDocument> FindManifests(
        LiteDatabase database,
        BrowserProfileSelection selection)
    {
        var documents = database
            .GetCollection<BsonDocument>(ManifestCollection)
            .FindAll()
            .Where(document =>
                string.Equals(
                    document["profileId"].AsString,
                    selection.ProfileId.Value,
                    StringComparison.Ordinal)
                && document["partitionKind"].AsInt32
                    == (int)selection.Partition.Kind
                && string.Equals(
                    document["partitionIdentity"].AsString,
                    selection.Partition.Identity,
                    StringComparison.Ordinal))
            .ToArray();
        foreach (var document in documents)
        {
            _ = FindManifest(database, document["_id"].AsString);
        }

        return documents;
    }

    private static string StorageId(BrowserProfileStateKey key)
    {
        var canonical = string.Join(
            '\0',
            key.Selection.ProfileId.Value,
            ((int)key.Selection.Partition.Kind).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            key.Selection.Partition.Identity,
            key.Route);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static long WriteArchive(ZipArchive archive, string sourceDirectory, ref string stage)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        long contentBytes = 0;
        while (pending.Count > 0)
        {
            stage = "enumerate-source";
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                entries++;
                if (entries > MaximumArchiveEntries)
                {
                    throw new IOException("The browser profile contains too many files.");
                }

                var info = File.GetAttributes(path);
                if ((info & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                    || new FileInfo(path).LinkTarget is not null
                    || new DirectoryInfo(path).LinkTarget is not null)
                {
                    throw new InvalidDataException(
                        "The browser profile contains an unsupported linked path.");
                }

                var relative = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if ((info & FileAttributes.Directory) != 0)
                {
                    archive.CreateEntry(relative.TrimEnd('/') + '/');
                    pending.Push(path);
                    continue;
                }

                var file = new FileInfo(path);
                contentBytes = checked(contentBytes + file.Length);
                if (contentBytes > MaximumExpandedBytes)
                {
                    throw new IOException("The browser profile exceeds the supported encrypted size.");
                }

                stage = "create-entry";
                var entry = archive.CreateEntry(relative, CompressionLevel.NoCompression);
                stage = "open-source";
                using var source = OpenArchiveSource(path);
                stage = "open-entry";
                using var destination = entry.Open();
                stage = "copy-source";
                source.CopyTo(destination);
                stage = "close-entry";
            }
        }

        return contentBytes;
    }

    private static FileStream OpenArchiveSource(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException exception)
        {
            exception.Data["Asura.BrowserSealSourceCategory"] = Path.GetFileName(path) switch
            {
                "LOCK" => "leveldb-lock",
                "first_party_sets.db" or "first_party_sets.db-journal" => "first-party-sets",
                "Ruleset Data" => "ruleset",
                "ranked_dicts" => "password-dictionary",
                "Cookies" or "Cookies-journal" => "cookies",
                "History" or "History-journal" => "history",
                "BrowserMetrics-spare.pma" => "metrics",
                _ => "other",
            };
            throw;
        }
    }

    private static void RestoreArchive(Stream source, string destinationDirectory)
    {
        using var archive = new ZipArchive(
            source,
            ZipArchiveMode.Read,
            leaveOpen: false,
            entryNameEncoding: Encoding.UTF8);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException(
                "The encrypted browser profile archive contains too many entries.");
        }

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(destinationDirectory));
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException(
                    "The encrypted browser profile archive is too large.");
            }

            var segments = entry.FullName.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                throw new InvalidDataException(
                    "The encrypted browser profile archive contains an empty path.");
            }

            var target = root;
            foreach (var segment in segments)
            {
                var safeSegment = Path.GetFileName(segment);
                if (string.IsNullOrWhiteSpace(safeSegment)
                    || !string.Equals(segment, safeSegment, StringComparison.Ordinal)
                    || string.Equals(segment, ".", StringComparison.Ordinal)
                    || string.Equals(segment, "..", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The encrypted browser profile archive contains an unsafe path.");
                }

                target = Path.Combine(target, safeSegment);
            }

            target = Path.GetFullPath(target);
            if (!target.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The encrypted browser profile archive escapes its runtime directory.");
            }

            if (entry.FullName.EndsWith('/'))
            {
                PrivateContentPathGuard.EnsurePrivateDirectory(target);
                continue;
            }

            PrivateContentPathGuard.EnsurePrivateDirectory(
                Path.GetDirectoryName(target)
                ?? throw new InvalidDataException(
                    "The encrypted browser profile entry has no parent directory."));
            using var input = entry.Open();
            using var output = PrivateContentPathGuard.CreatePrivateFile(
                target,
                FileShare.None);
            input.CopyTo(output);
        }
    }

    private static void PrepareEmptyDestination(string destinationDirectory)
    {
        PrivateContentPathGuard.EnsurePrivateDirectory(destinationDirectory);
        if (Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
        {
            throw new InvalidOperationException(
                "A browser profile can be restored only into an empty runtime directory.");
        }
    }

    private static void ValidateSourceRoot(string sourceDirectory)
    {
        PrivateContentPathGuard.ValidatePrivateDirectory(sourceDirectory);
    }

    private static void DeleteOwnedTreeContents(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static void DeletePrivateFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        PrivateContentPathGuard.ValidatePrivateFile(path);
        File.Delete(path);
    }

    private static void RemoveUnreferencedBlobs(LiteDatabase database)
    {
        var referenced = database
            .GetCollection<BsonDocument>(ManifestCollection)
            .FindAll()
            .Select(document => document["blobId"].AsString)
            .ToHashSet(StringComparer.Ordinal);
        var storage = database.GetStorage<string>();
        foreach (var file in storage.FindAll())
        {
            if (!referenced.Contains(file.Id))
            {
                storage.Delete(file.Id);
            }
        }
    }

    private static InvalidDataException InvalidContainer(LiteException exception) =>
        new(
            "The encrypted browser profile container is unreadable or uses a different key.",
            exception);
}
