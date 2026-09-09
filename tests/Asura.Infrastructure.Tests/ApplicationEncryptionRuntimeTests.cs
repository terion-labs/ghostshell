using System.Text;
using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure.Tests;

/// <summary>
/// Application encryption against the real engine and a real database file:
/// enabling converts the disk, the keys live in the vault, and a restart
/// finds everything exactly as the switch left it.
/// </summary>
public sealed class ApplicationEncryptionRuntimeTests : IAsyncDisposable
{
    private const string ConfigKeyReference = "app.security.config-database-key";
    private const string CacheKeyReference = "app.security.preview-cache-key";

    private static readonly SecretUsePurpose Purpose = new(
        SecretUseKind.PlatformMaintenance,
        SecretUsePurpose.GlobalTargetId);

    private readonly string _root =
        Directory.CreateTempSubdirectory("asura-app-encryption").FullName;

    private readonly PersistentTestVault _vault = new();

    /// <summary>
    /// The in-memory vault presenting as OS-protected: encryption rightly
    /// refuses a keystore whose keys die with the process, and these tests
    /// are about the conversion, not the keystore.
    /// </summary>
    private sealed class PersistentTestVault : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();

        public SecretVaultAvailability Availability => new(
            SecretVaultAvailabilityState.Available,
            SecretVaultPersistenceKind.OsProtectedPersistent,
            SecretVaultCapabilities.All,
            "test",
            "test_persistent",
            "Test vault presenting as persistent.");

        public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
            CreateSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) =>
            _inner.CreateAsync(request, material, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken cancellationToken) =>
            _inner.ResolveAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
            ReplaceSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) =>
            _inner.ReplaceAsync(request, material, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
            RelabelSecretRequest request,
            CancellationToken cancellationToken) =>
            _inner.RelabelAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
            DeleteSecretRequest request,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
            GetSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            _inner.GetMetadataAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
            ListSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            _inner.ListMetadataAsync(request, cancellationToken);

        public async ValueTask<string?> ReadAsync(string reference)
        {
            var result = await _inner.ResolveAsync(
                new ResolveSecretRequest(
                    new SecretRef(reference),
                    SecretScope.Global,
                    Purpose),
                CancellationToken.None);
            if (result is not SecretVaultResult<SecretMaterial>.Success success)
            {
                return null;
            }

            using var material = success.Value;
            var buffer = new byte[material.Length];
            material.CopyTo(buffer);
            return Encoding.UTF8.GetString(buffer);
        }

        public void Dispose() => _inner.Dispose();
    }
    private readonly List<AsuraDatabase> _databases = [];

    private string DatabasePath => Path.Combine(_root, "asura.db");

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await database.DisposeAsync();
        }

        _vault.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_fresh_profile_is_encrypted_before_its_first_database_open()
    {
        var (runtime, database) = Compose();
        Assert.False(File.Exists(DatabasePath));

        await runtime.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);

        Assert.True(runtime.IsEnabled);
        Assert.Null(runtime.StartupError);
        Assert.NotNull(runtime.PersistentCachePassword);
        Assert.False(
            LooksLikePlainSqlite(),
            "The fresh database still announces itself as plain SQLite.");
        await WriteSentinelAsync(database);
        Assert.Equal("sentinel-value", await ReadSentinelAsync(database));
    }

    [Fact]
    public async Task A_fresh_profile_fails_closed_without_a_persistent_keystore()
    {
        using var vault = new InMemorySecretVault();
        var runtime = new ApplicationEncryptionRuntime(
            vault,
            DatabasePath,
            () => throw new InvalidOperationException(
                "An unsupported default must not create a database."));

        await runtime.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);

        Assert.False(runtime.IsEnabled);
        Assert.Equal(runtime.UnsupportedReason, runtime.StartupError);
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task A_zero_byte_profile_is_initialized_encrypted()
    {
        await File.WriteAllBytesAsync(DatabasePath, []);
        var (runtime, database) = Compose();

        await runtime.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);

        Assert.True(runtime.IsEnabled);
        Assert.Null(runtime.StartupError);
        Assert.False(LooksLikePlainSqlite());
        await WriteSentinelAsync(database);
        Assert.Equal("sentinel-value", await ReadSentinelAsync(database));
    }

    [Fact]
    public async Task A_zero_byte_profile_stays_empty_when_the_keystore_is_unavailable()
    {
        await File.WriteAllBytesAsync(DatabasePath, []);
        using var vault = new InMemorySecretVault();
        var runtime = new ApplicationEncryptionRuntime(
            vault,
            DatabasePath,
            () => throw new InvalidOperationException("The database must not be opened."));

        await runtime.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);

        Assert.False(runtime.IsEnabled);
        Assert.Equal(runtime.UnsupportedReason, runtime.StartupError);
        Assert.Equal(0, new FileInfo(DatabasePath).Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public async Task A_nonempty_short_profile_fails_closed_without_modification(int length)
    {
        var original = Enumerable.Range(1, length).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(DatabasePath, original);
        var (runtime, _) = Compose();

        await runtime.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);

        Assert.False(runtime.IsEnabled);
        Assert.Contains("truncated", runtime.StartupError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(DatabasePath));
    }

    [Fact]
    public async Task An_existing_valid_plaintext_profile_preserves_the_explicit_disabled_state()
    {
        var (runtime, database) = Compose();
        await WriteSentinelAsync(database);

        await runtime.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);

        Assert.False(runtime.IsEnabled);
        Assert.Null(runtime.StartupError);
        Assert.True(LooksLikePlainSqlite());
        Assert.Equal("sentinel-value", await ReadSentinelAsync(database));
    }

    [Fact]
    public async Task A_malformed_full_header_fails_closed_without_modification()
    {
        var original = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        await File.WriteAllBytesAsync(DatabasePath, original);
        var (runtime, _) = Compose();

        await runtime.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);

        Assert.True(runtime.IsEnabled);
        Assert.Contains("key is not in", runtime.StartupError, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(DatabasePath));
    }

    [Fact]
    public async Task Enabling_encrypts_the_database_in_place_and_a_restart_still_opens_it()
    {
        var (runtime, database) = Compose();
        await WriteSentinelAsync(database);

        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));

        Assert.True(runtime.IsEnabled);
        Assert.False(LooksLikePlainSqlite(), "The database still announces itself as plain SQLite.");
        var image = await File.ReadAllBytesAsync(DatabasePath);
        Assert.True(
            image.AsSpan().IndexOf("sentinel-value"u8) < 0,
            "The sentinel row is readable in the encrypted database file.");
        Assert.Equal("sentinel-value", await ReadSentinelAsync(database));

        // A restart: a fresh runtime over the same disk and vault.
        var (rebooted, rebootedDatabase) = Compose();
        await rebooted.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);
        Assert.True(rebooted.IsEnabled);
        Assert.Null(rebooted.StartupError);
        Assert.Equal("sentinel-value", await ReadSentinelAsync(rebootedDatabase));
    }

    [Fact]
    public async Task Disabling_decrypts_in_place_and_forgets_the_keys()
    {
        var (runtime, database) = Compose();
        await WriteSentinelAsync(database);
        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));

        Assert.Null(await runtime.SetEnabledAsync(false, CancellationToken.None));

        Assert.False(runtime.IsEnabled);
        Assert.True(LooksLikePlainSqlite());
        Assert.Equal("sentinel-value", await ReadSentinelAsync(database));
        // The keys are gone: a fresh runtime sees a plain database and has
        // nothing to resolve.
        var (rebooted, _) = Compose();
        await rebooted.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);
        Assert.False(rebooted.IsEnabled);
        Assert.Null(rebooted.PersistentCachePassword);
    }

    [Fact]
    public async Task An_encrypted_database_whose_key_is_gone_is_a_startup_error_not_a_crash()
    {
        var (runtime, database) = Compose();
        await WriteSentinelAsync(database);
        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));

        // The keystore is lost — a new vault knows nothing.
        using var emptyVault = new PersistentTestVault();
        var orphaned = new ApplicationEncryptionRuntime(
            emptyVault,
            DatabasePath,
            () => throw new InvalidOperationException("The database must not be touched."));
        await orphaned.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);

        Assert.True(orphaned.IsEnabled);
        Assert.NotNull(orphaned.StartupError);
    }

    [Fact]
    public async Task The_cache_password_exists_exactly_while_encryption_is_enabled()
    {
        var (runtime, database) = Compose();
        await WriteSentinelAsync(database);
        Assert.Null(runtime.PersistentCachePassword);

        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));
        Assert.NotNull(runtime.PersistentCachePassword);

        Assert.Null(await runtime.SetEnabledAsync(false, CancellationToken.None));
        Assert.Null(runtime.PersistentCachePassword);
    }

    [Theory]
    [InlineData((int)ApplicationEncryptionRuntime.RekeyCheckpoint.AfterRekey)]
    [InlineData((int)ApplicationEncryptionRuntime.RekeyCheckpoint.BeforeVerification)]
    [InlineData((int)ApplicationEncryptionRuntime.RekeyCheckpoint.BeforeIntegrityCheck)]
    public async Task An_ambiguous_post_rekey_failure_retains_keys_for_restart_recovery(
        int faultPointValue)
    {
        var faultPoint = (ApplicationEncryptionRuntime.RekeyCheckpoint)faultPointValue;
        var failAtCheckpoint = true;
        var (runtime, database) = Compose(checkpoint =>
        {
            if (failAtCheckpoint && checkpoint == faultPoint)
            {
                failAtCheckpoint = false;
                throw new SqliteException("injected post-rekey fault", 10);
            }
        });
        await WriteSentinelAsync(database);

        var error = await runtime.SetEnabledAsync(true, CancellationToken.None);

        Assert.Contains("retained", error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await _vault.ReadAsync(ConfigKeyReference));
        Assert.NotNull(await _vault.ReadAsync(CacheKeyReference));
        Assert.Contains(
            "Restart",
            await runtime.SetEnabledAsync(true, CancellationToken.None),
            StringComparison.OrdinalIgnoreCase);

        var (restarted, restartedDatabase) = Compose();
        await restarted.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);
        Assert.True(restarted.IsEnabled);
        Assert.Null(restarted.StartupError);
        Assert.Equal("sentinel-value", await ReadSentinelAsync(restartedDatabase));
    }

    [Fact]
    public async Task An_ambiguous_disable_failure_retains_the_active_keys_until_restart()
    {
        var faultArmed = false;
        var (runtime, database) = Compose(checkpoint =>
        {
            if (faultArmed
                && checkpoint is ApplicationEncryptionRuntime.RekeyCheckpoint.AfterRekey)
            {
                faultArmed = false;
                throw new SqliteException("injected post-decrypt fault", 10);
            }
        });
        await WriteSentinelAsync(database);
        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));
        faultArmed = true;

        var error = await runtime.SetEnabledAsync(false, CancellationToken.None);

        Assert.Contains("retained", error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await _vault.ReadAsync(ConfigKeyReference));
        Assert.NotNull(await _vault.ReadAsync(CacheKeyReference));
        Assert.Contains(
            "Restart",
            await runtime.SetEnabledAsync(false, CancellationToken.None),
            StringComparison.OrdinalIgnoreCase);

        var (restarted, restartedDatabase) = Compose();
        await restarted.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);
        Assert.False(restarted.IsEnabled);
        Assert.Null(restarted.StartupError);
        Assert.True(LooksLikePlainSqlite());
        Assert.Equal("sentinel-value", await ReadSentinelAsync(restartedDatabase));
    }

    [Fact]
    public async Task A_definite_pre_rekey_failure_removes_unused_candidate_keys()
    {
        var (runtime, database) = Compose(checkpoint =>
        {
            if (checkpoint is ApplicationEncryptionRuntime.RekeyCheckpoint.BeforeRekey)
            {
                throw new SqliteException("injected pre-rekey fault", 10);
            }
        });
        await WriteSentinelAsync(database);

        var error = await runtime.SetEnabledAsync(true, CancellationToken.None);

        Assert.DoesNotContain("retained", error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await _vault.ReadAsync(ConfigKeyReference));
        Assert.Null(await _vault.ReadAsync(CacheKeyReference));
        Assert.True(LooksLikePlainSqlite());
        Assert.Equal("sentinel-value", await ReadSentinelAsync(database));
    }

    [Fact]
    public async Task Disabling_retains_and_reuses_the_key_while_encrypted_backups_exist()
    {
        var (runtime, database) = Compose();
        await WriteSentinelAsync(database);
        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));
        var activeKey = Assert.IsType<string>(await _vault.ReadAsync(ConfigKeyReference));
        var backupDirectory = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backupDirectory);
        File.Copy(
            DatabasePath,
            Path.Combine(backupDirectory, "asura-before-v99-retention.db"));

        Assert.Null(await runtime.SetEnabledAsync(false, CancellationToken.None));

        Assert.False(runtime.IsEnabled);
        Assert.Equal(activeKey, await _vault.ReadAsync(ConfigKeyReference));
        Assert.Null(await _vault.ReadAsync(CacheKeyReference));
        Assert.True(LooksLikePlainSqlite());

        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));
        Assert.Equal(activeKey, await _vault.ReadAsync(ConfigKeyReference));
        Assert.True(runtime.IsEnabled);
        Assert.Equal("sentinel-value", await ReadSentinelAsync(database));
    }

    private (ApplicationEncryptionRuntime Runtime, AsuraDatabase Database) Compose(
        Action<ApplicationEncryptionRuntime.RekeyCheckpoint>? rekeyCheckpoint = null)
    {
        ApplicationEncryptionRuntime? runtime = null;
        var options = new SqliteStorageOptions(
            DatabasePath,
            acquireProfileLock: false)
        {
            PasswordProvider = () => runtime!.ConfigDatabasePassword,
        };
        var database = new AsuraDatabase(options, TimeProvider.System);
        _databases.Add(database);
        runtime = rekeyCheckpoint is null
            ? new ApplicationEncryptionRuntime(_vault, DatabasePath, () => database)
            : new ApplicationEncryptionRuntime(
                _vault,
                DatabasePath,
                () => database,
                rekeyCheckpoint);
        return (runtime, database);
    }

    private static async Task WriteSentinelAsync(AsuraDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE session_restore_preference SET restore_sessions_on_start = 1;
            INSERT INTO definitions(
                kind, id, schema_version, revision, name, payload_json,
                created_utc, updated_utc)
            VALUES (
                'probe', 'sentinel', 1, 1, 'sentinel-value', '{}',
                '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadSentinelAsync(AsuraDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM definitions WHERE id = 'sentinel';";
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private bool LooksLikePlainSqlite()
    {
        using var file = File.OpenRead(DatabasePath);
        var header = new byte[15];
        file.ReadExactly(header);
        return string.Equals(Encoding.ASCII.GetString(header), "SQLite format 3", StringComparison.Ordinal);
    }
}
