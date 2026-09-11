using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;
using Asura.Files;
using LiteDB;

namespace Asura.BrowserStorageAcceptance;

internal static class Program
{
    public static int Main(string[] arguments)
    {
        if (RuntimeFeature.IsDynamicCodeSupported || arguments is not [var mode, var root])
        {
            Console.Error.WriteLine("Run the Native AOT binary with write/read/strip and a private test directory.");
            return 1;
        }

        var source = Path.Combine(root, "source");
        var key = new BrowserProfileStateKey(
            new BrowserProfileSelection(new BrowserProfileId("acceptance"), BrowserProfileKey.Global),
            "engine");
        using var store = new EncryptedBrowserProfileStateStore(Path.Combine(root, "encrypted"), new Encryption());
        if (string.Equals(mode, "write", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(source);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            File.WriteAllBytes(Path.Combine(source, "Cookies"), RandomNumberGenerator.GetBytes(700_000));
            store.Seal(key, source);
        }
        else if (string.Equals(mode, "read", StringComparison.Ordinal))
        {
            var restored = Path.Combine(root, "restored");
            store.Restore(key, restored);
            if (!File.ReadAllBytes(Path.Combine(source, "Cookies")).AsSpan()
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(restored, "Cookies"))))
            {
                throw new InvalidDataException("Native AOT archive did not round-trip.");
            }
            store.Seal(key, restored);
            Console.WriteLine("Native AOT browser storage survived a process restart and rewrite.");
        }
        else if (string.Equals(mode, "strip", StringComparison.Ordinal))
        {
            using var database = new LiteDatabase(new ConnectionString
            {
                Filename = Path.Combine(root, "encrypted", "browser-profiles.db"),
                Password = new Encryption().PersistentCachePassword,
            });
            var files = database.GetCollection<BsonDocument>("_files");
            files.DeleteAll();
            files.Insert(new BsonDocument { ["_id"] = ObjectId.NewObjectId() });
        }
        else
        {
            return 1;
        }
        return 0;
    }

    private sealed class Encryption : IApplicationEncryption
    {
        public bool IsSupported => true;
        public bool IsEnabled => true;
        public bool AwaitingUnlock => false;
        public string? UnsupportedReason => null;
        // A fixed key only for the synthetic acceptance fixture; never an OS profile.
        public string? PersistentCachePassword => "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        public event EventHandler? Changed { add { } remove { } }
        public ValueTask<string?> SetEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
