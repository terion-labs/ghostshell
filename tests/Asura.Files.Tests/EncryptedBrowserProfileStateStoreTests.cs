using System.Text;
using Asura.Application;
using Asura.Core;
using LiteDB;

namespace Asura.Files.Tests;

public sealed class EncryptedBrowserProfileStateStoreTests : IDisposable
{
    private readonly string _root = Directory
        .CreateTempSubdirectory("asura-browser-state")
        .FullName;

    [Fact]
    public void CompleteChromiumTreeRoundTripsAcrossStoreInstances()
    {
        var encryption = new TestApplicationEncryption();
        var storeDirectory = Path.Combine(_root, "store");
        var source = Path.Combine(_root, "source");
        CreatePrivateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "Default", "IndexedDB"));
        Directory.CreateDirectory(Path.Combine(source, "Default", "Local Storage"));
        File.WriteAllText(
            Path.Combine(source, "Default", "Cookies"),
            "session-cookie-private-marker");
        File.WriteAllBytes(
            Path.Combine(source, "Default", "IndexedDB", "000003.log"),
            [0, 1, 2, 3, 255]);
        File.WriteAllText(
            Path.Combine(source, "Default", "Local Storage", "state"),
            "signed-in=true");
        var key = StateKey("profile.persisted", "local");

        using (var writer = new EncryptedBrowserProfileStateStore(
                   storeDirectory,
                   encryption))
        {
            Assert.True(writer.Seal(key, source) > 0);
        }

        var container = File.ReadAllBytes(
            Path.Combine(storeDirectory, "browser-profiles.db"));
        Assert.DoesNotContain(
            "session-cookie-private-marker",
            Encoding.UTF8.GetString(container),
            StringComparison.Ordinal);

        var restored = Path.Combine(_root, "restored");
        using var reader = new EncryptedBrowserProfileStateStore(
            storeDirectory,
            encryption);
        reader.Restore(key, restored);

        Assert.Equal(
            "session-cookie-private-marker",
            File.ReadAllText(Path.Combine(restored, "Default", "Cookies")));
        Assert.Equal(
            new byte[] { 0, 1, 2, 3, 255 },
            File.ReadAllBytes(
                Path.Combine(restored, "Default", "IndexedDB", "000003.log")));
        Assert.Equal(
            "signed-in=true",
            File.ReadAllText(
                Path.Combine(restored, "Default", "Local Storage", "state")));
    }

    [Fact]
    public void ProfilesPartitionsAndRoutesRemainIsolated()
    {
        var encryption = new TestApplicationEncryption();
        using var store = new EncryptedBrowserProfileStateStore(
            Path.Combine(_root, "isolated-store"),
            encryption);
        var first = StateKey("profile.first", "local");
        var routed = StateKey("profile.first", "ssh:server");
        var second = StateKey("profile.second", "local");
        SealMarker(store, first, "first");
        SealMarker(store, routed, "routed");
        SealMarker(store, second, "second");

        Assert.Equal(
            [first, routed],
            store.ListKeys(first.Selection).OrderBy(item => item.Route, StringComparer.Ordinal));
        Assert.True(store.Delete(first.Selection) > 0);
        Assert.Empty(store.ListKeys(first.Selection));

        var destination = Path.Combine(_root, "second-restored");
        store.Restore(second, destination);
        Assert.Equal("second", File.ReadAllText(Path.Combine(destination, "marker")));
    }

    [Fact]
    public void DisablingEncryptionDeletesSavedBrowserSessions()
    {
        var encryption = new TestApplicationEncryption();
        var storeDirectory = Path.Combine(_root, "deleted-store");
        using var store = new EncryptedBrowserProfileStateStore(
            storeDirectory,
            encryption);
        SealMarker(store, StateKey("profile.deleted", "local"), "secret");
        Assert.True(File.Exists(Path.Combine(storeDirectory, "browser-profiles.db")));

        encryption.IsEnabled = false;
        encryption.PersistentCachePassword = null;
        encryption.RaiseChanged();

        Assert.False(File.Exists(Path.Combine(storeDirectory, "browser-profiles.db")));
        Assert.False(store.IsRetentionEnabled);
    }

    [Fact]
    public void WrongKeyIsReportedAsAnUnreadableEncryptedContainer()
    {
        var storeDirectory = Path.Combine(_root, "wrong-key-store");
        var firstEncryption = new TestApplicationEncryption();
        var key = StateKey("profile.wrong-key", "local");
        using (var writer = new EncryptedBrowserProfileStateStore(
                   storeDirectory,
                   firstEncryption))
        {
            SealMarker(writer, key, "protected");
        }

        var otherEncryption = new TestApplicationEncryption
        {
            PersistentCachePassword =
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
        };
        using var reader = new EncryptedBrowserProfileStateStore(
            storeDirectory,
            otherEncryption);
        var destination = Path.Combine(_root, "wrong-key-destination");

        var error = Assert.Throws<InvalidDataException>(() =>
            reader.Restore(key, destination));

        Assert.Contains("different key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepairsMetadataStrippedByEarlierNativeAotBuildsWithoutLosingArchiveBytes()
    {
        var encryption = new TestApplicationEncryption();
        var directory = Path.Combine(_root, "trimmed-store");
        var source = Path.Combine(_root, "trimmed-source");
        CreatePrivateDirectory(source);
        var content = new byte[700_000];
        new Random(42).NextBytes(content);
        File.WriteAllBytes(Path.Combine(source, "Cookies"), content);
        var key = StateKey("profile.trimmed", "local");
        using (var writer = new EncryptedBrowserProfileStateStore(directory, encryption))
        {
            writer.Seal(key, source);
        }

        using (var database = new LiteDatabase(new ConnectionString
        {
            Filename = Path.Combine(directory, "browser-profiles.db"),
            Password = encryption.PersistentCachePassword,
        }))
        {
            var files = database.GetCollection<BsonDocument>("_files");
            files.DeleteAll();
            files.Insert(new BsonDocument { ["_id"] = ObjectId.NewObjectId() });
        }

        using var repaired = new EncryptedBrowserProfileStateStore(directory, encryption);
        var restored = Path.Combine(_root, "trimmed-restored");
        repaired.Restore(key, restored);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(restored, "Cookies")));
        Assert.Contains(key, repaired.ListKeys(key.Selection));
        repaired.Seal(key, restored);
        var reopened = Path.Combine(_root, "trimmed-reopened");
        repaired.Restore(key, reopened);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(reopened, "Cookies")));
    }

    [Fact]
    public void IncompleteLegacyArchiveIsPreservedWhenRepairCannotFinish()
    {
        var encryption = new TestApplicationEncryption();
        var directory = Path.Combine(_root, "incomplete-trimmed-store");
        var key = StateKey("profile.incomplete", "local");
        using (var writer = new EncryptedBrowserProfileStateStore(directory, encryption))
        {
            SealMarker(writer, key, "saved-session");
        }

        var connection = new ConnectionString
        {
            Filename = Path.Combine(directory, "browser-profiles.db"),
            Password = encryption.PersistentCachePassword,
        };
        var legacyId = ObjectId.NewObjectId();
        using (var database = new LiteDatabase(connection))
        {
            var files = database.GetCollection<BsonDocument>("_files");
            files.DeleteAll();
            files.Insert(new BsonDocument { ["_id"] = legacyId });
            database.GetCollection<BsonDocument>("_chunks").DeleteAll();
        }

        using var reader = new EncryptedBrowserProfileStateStore(directory, encryption);
        Assert.Throws<InvalidDataException>(() => reader.Restore(key, Path.Combine(_root, "incomplete-restore")));
        using var preserved = new LiteDatabase(connection);
        var retained = Assert.Single(preserved.GetCollection<BsonDocument>("_files").FindAll());
        Assert.Equal(legacyId, retained["_id"].AsObjectId);
        Assert.Single(preserved.GetCollection<BsonDocument>("browser_profile_state").FindAll());
    }

    [Fact]
    public void CleanupFailureDoesNotDeleteTheNewlyCommittedArchive()
    {
        var encryption = new TestApplicationEncryption();
        var directory = Path.Combine(_root, "cleanup-failure-store");
        var key = StateKey("profile.cleanup", "local");
        using var store = new EncryptedBrowserProfileStateStore(directory, encryption);
        SealMarker(store, key, "previous");
        using (var database = new LiteDatabase(new ConnectionString
        {
            Filename = Path.Combine(directory, "browser-profiles.db"),
            Password = encryption.PersistentCachePassword,
        }))
        {
            // An unrelated malformed blob fails typed enumeration during cleanup.
            database.GetCollection<BsonDocument>("_files").Insert(new BsonDocument
            {
                ["_id"] = ObjectId.NewObjectId(),
                ["unexpected"] = true,
            });
        }

        Assert.Throws<InvalidCastException>(() => SealMarker(store, key, "newly-committed"));
        var restored = Path.Combine(_root, "committed-after-cleanup-failure");
        store.Restore(key, restored);
        Assert.Equal("newly-committed", File.ReadAllText(Path.Combine(restored, "marker")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void SealMarker(
        EncryptedBrowserProfileStateStore store,
        BrowserProfileStateKey key,
        string marker)
    {
        var source = Path.Combine(_root, Guid.NewGuid().ToString("n"));
        CreatePrivateDirectory(source);
        File.WriteAllText(Path.Combine(source, "marker"), marker);
        store.Seal(key, source);
    }

    private static BrowserProfileStateKey StateKey(string profileId, string route)
    {
        var id = new BrowserProfileId(profileId);
        return new BrowserProfileStateKey(
            new BrowserProfileSelection(id, BrowserProfileKey.ForNamed(id.Value)),
            route);
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }

    private sealed class TestApplicationEncryption : IApplicationEncryption
    {
        public bool IsSupported => true;

        public bool IsEnabled { get; set; } = true;

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
}
