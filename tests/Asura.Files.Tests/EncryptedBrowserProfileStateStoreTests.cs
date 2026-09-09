using System.Text;
using Asura.Application;
using Asura.Core;

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
