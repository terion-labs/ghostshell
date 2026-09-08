using GhostShell.Application;

namespace GhostShell.Infrastructure.Tests;

/// <summary>
/// The PIN gate: verification is peppered, misses meter into a persisted
/// doubling delay, and a fresh process finds the gate exactly as the last
/// one left it.
/// </summary>
public sealed class StartupProtectionRuntimeTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("ghostshell-startup-protection").FullName;

    private readonly PersistentVault _vault = new();
    private readonly ManualClock _clock = new(DateTimeOffset.Parse("2026-08-04T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _vault.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private StartupProtectionRuntime Create() => new(_vault, _root, _clock);

    [Fact]
    public async Task Enabling_locks_future_starts_and_the_right_pin_unlocks()
    {
        var first = Create();
        Assert.False(first.IsEnabled);
        Assert.False(first.IsLocked);

        Assert.Null(await first.EnableAsync("4812", CancellationToken.None));
        Assert.True(first.IsEnabled);
        // The person who just chose the PIN is not asked to repeat it.
        Assert.False(first.IsLocked);

        // A fresh start finds the gate down.
        var restarted = Create();
        Assert.True(restarted.IsEnabled);
        Assert.True(restarted.IsLocked);
        Assert.False(await restarted.TryUnlockAsync("0000", CancellationToken.None));
        Assert.True(restarted.IsLocked);
        Assert.True(await restarted.TryUnlockAsync("4812", CancellationToken.None));
        Assert.False(restarted.IsLocked);
    }

    [Fact]
    public async Task A_short_pin_is_refused()
    {
        var runtime = Create();
        Assert.NotNull(await runtime.EnableAsync("12", CancellationToken.None));
        Assert.False(runtime.IsEnabled);
    }

    [Fact]
    public async Task Repeated_misses_earn_a_persisted_doubling_delay()
    {
        var runtime = Create();
        Assert.Null(await runtime.EnableAsync("4812", CancellationToken.None));
        runtime.Lock();

        for (var miss = 0; miss < 5; miss++)
        {
            Assert.False(await runtime.TryUnlockAsync("0000", CancellationToken.None));
            Assert.Equal(0, runtime.RetryDelaySeconds);
        }

        // The sixth miss starts the meter.
        Assert.False(await runtime.TryUnlockAsync("0000", CancellationToken.None));
        Assert.Equal(30, runtime.RetryDelaySeconds);

        // Even the right PIN waits out the delay.
        Assert.False(await runtime.TryUnlockAsync("4812", CancellationToken.None));

        // A restart does not reset the meter: it is on disk.
        var restarted = Create();
        Assert.True(restarted.RetryDelaySeconds > 0);

        // Waiting does.
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(0, restarted.RetryDelaySeconds);
        Assert.True(await restarted.TryUnlockAsync("4812", CancellationToken.None));
        Assert.Equal(0, restarted.RetryDelaySeconds);
    }

    [Fact]
    public async Task The_file_alone_verifies_nothing_without_the_keystore_pepper()
    {
        var runtime = Create();
        Assert.Null(await runtime.EnableAsync("4812", CancellationToken.None));

        // The protection file survives; the keystore does not.
        using var strangerVault = new PersistentVault();
        var stranger = new StartupProtectionRuntime(strangerVault, _root, _clock);
        Assert.True(stranger.IsEnabled);
        Assert.False(await stranger.TryUnlockAsync("4812", CancellationToken.None));
    }

    [Fact]
    public async Task Disabling_requires_the_pin_and_removes_the_gate()
    {
        var runtime = Create();
        Assert.Null(await runtime.EnableAsync("4812", CancellationToken.None));

        Assert.NotNull(await runtime.DisableAsync("0000", CancellationToken.None));
        Assert.True(runtime.IsEnabled);

        Assert.Null(await runtime.DisableAsync("4812", CancellationToken.None));
        Assert.False(runtime.IsEnabled);
        Assert.False(Create().IsEnabled);
    }

    [Fact]
    public async Task The_lock_timeout_is_kept_with_the_gate()
    {
        var runtime = Create();
        Assert.Null(await runtime.EnableAsync("4812", CancellationToken.None));
        await runtime.SetLockTimeoutAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), Create().LockTimeout);
    }

    [Fact]
    public async Task Every_state_change_is_announced()
    {
        var runtime = Create();
        var announcements = 0;
        runtime.Changed += (_, _) => announcements++;

        Assert.Null(await runtime.EnableAsync("4812", CancellationToken.None));
        runtime.Lock();
        Assert.True(await runtime.TryUnlockAsync("4812", CancellationToken.None));
        await runtime.SetLockTimeoutAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Null(await runtime.DisableAsync("4812", CancellationToken.None));

        // Enable, lock, unlock, timeout, the disable's own unlock, disable.
        Assert.True(announcements >= 5);
    }

    [Fact]
    public async Task Enabling_protection_with_encryption_on_seals_the_keys_and_the_pin_releases_them()
    {
        // A real encrypted database whose keys end up sealed under the PIN.
        var databasePath = Path.Combine(_root, "ghostshell.db");
        var (encryption, database) = ComposeEncryption(databasePath);
        await WriteProbeRowAsync(database);
        Assert.Null(await encryption.SetEnabledAsync(true, CancellationToken.None));

        var protection = new StartupProtectionRuntime(_vault, _root, _clock, encryption);
        Assert.Null(await protection.EnableAsync("4812", CancellationToken.None));
        Assert.True(protection.HoldsWrappedKeys);

        // The keystore holds no key copies any more: a fresh encryption
        // runtime over the same vault cannot open the database on its own...
        var (rebooted, rebootedDatabase) = ComposeEncryption(databasePath);
        var rebootedProtection = new StartupProtectionRuntime(_vault, _root, _clock, rebooted);
        await rebooted.InitializeAsync(
            wrappedKeysPending: rebootedProtection.HoldsWrappedKeys,
            CancellationToken.None);
        Assert.True(rebooted.AwaitingUnlock);
        Assert.Null(rebooted.StartupError);

        // ...until the PIN arrives.
        Assert.True(await rebootedProtection.TryUnlockAsync("4812", CancellationToken.None));
        Assert.False(rebooted.AwaitingUnlock);
        Assert.True(rebooted.IsEnabled);
        Assert.Equal("probe", await ReadProbeRowAsync(rebootedDatabase));
    }

    [Fact]
    public async Task The_wrong_pin_releases_nothing()
    {
        var databasePath = Path.Combine(_root, "ghostshell.db");
        var (encryption, database) = ComposeEncryption(databasePath);
        await WriteProbeRowAsync(database);
        Assert.Null(await encryption.SetEnabledAsync(true, CancellationToken.None));
        var protection = new StartupProtectionRuntime(_vault, _root, _clock, encryption);
        Assert.Null(await protection.EnableAsync("4812", CancellationToken.None));

        var (rebooted, _) = ComposeEncryption(databasePath);
        var rebootedProtection = new StartupProtectionRuntime(_vault, _root, _clock, rebooted);
        await rebooted.InitializeAsync(
            wrappedKeysPending: true,
            CancellationToken.None);

        Assert.False(await rebootedProtection.TryUnlockAsync("0000", CancellationToken.None));
        Assert.True(rebooted.AwaitingUnlock);
        Assert.Null(rebooted.PersistentCachePassword);
    }

    [Theory]
    [InlineData("app.security.config-database-key")]
    [InlineData("app.security.preview-cache-key")]
    public async Task Partial_key_deletion_is_reported_and_sealed_keys_remain_recoverable(string refusedReference)
    {
        var databasePath = Path.Combine(_root, "ghostshell.db");
        var (encryption, database) = ComposeEncryption(databasePath);
        await WriteProbeRowAsync(database);
        Assert.Null(await encryption.SetEnabledAsync(true, CancellationToken.None));
        _vault.RefusedDeleteReference = refusedReference;
        var protection = new StartupProtectionRuntime(_vault, _root, _clock, encryption);

        var error = await protection.EnableAsync("4812", CancellationToken.None);

        Assert.Contains("incomplete", error, StringComparison.Ordinal);
        Assert.True(protection.HoldsWrappedKeys);
        var (rebooted, rebootedDatabase) = ComposeEncryption(databasePath);
        var restarted = new StartupProtectionRuntime(_vault, _root, _clock, rebooted);
        await rebooted.InitializeAsync(restarted.HoldsWrappedKeys, CancellationToken.None);
        Assert.True(await restarted.TryUnlockAsync("4812", CancellationToken.None));
        Assert.Equal("probe", await ReadProbeRowAsync(rebootedDatabase));
        Assert.NotNull(await restarted.SealEncryptionKeysAsync("4812", CancellationToken.None));

        _vault.RefusedDeleteReference = null;
        Assert.Null(await restarted.SealEncryptionKeysAsync("4812", CancellationToken.None));
        Assert.True(restarted.HoldsWrappedKeys);
    }

    [Fact]
    public async Task Disabling_protection_returns_the_keys_to_the_keystore()
    {
        var databasePath = Path.Combine(_root, "ghostshell.db");
        var (encryption, database) = ComposeEncryption(databasePath);
        await WriteProbeRowAsync(database);
        Assert.Null(await encryption.SetEnabledAsync(true, CancellationToken.None));
        var protection = new StartupProtectionRuntime(_vault, _root, _clock, encryption);
        Assert.Null(await protection.EnableAsync("4812", CancellationToken.None));

        Assert.Null(await protection.DisableAsync("4812", CancellationToken.None));

        // A restart needs nothing but the keystore again.
        var (rebooted, rebootedDatabase) = ComposeEncryption(databasePath);
        await rebooted.InitializeAsync(
            wrappedKeysPending: false,
            CancellationToken.None);
        Assert.Null(rebooted.StartupError);
        Assert.True(rebooted.IsEnabled);
        Assert.Equal("probe", await ReadProbeRowAsync(rebootedDatabase));
    }

    [Fact]
    public async Task Turning_encryption_on_under_standing_protection_seals_through_the_editor_seam()
    {
        var databasePath = Path.Combine(_root, "ghostshell.db");
        var (encryption, database) = ComposeEncryption(databasePath);
        await WriteProbeRowAsync(database);
        var protection = new StartupProtectionRuntime(_vault, _root, _clock, encryption);
        Assert.Null(await protection.EnableAsync("4812", CancellationToken.None));
        Assert.False(protection.HoldsWrappedKeys);

        Assert.Null(await encryption.SetEnabledAsync(true, CancellationToken.None));
        Assert.Null(await protection.SealEncryptionKeysAsync("4812", CancellationToken.None));

        Assert.True(protection.HoldsWrappedKeys);
        var (rebooted, _) = ComposeEncryption(databasePath);
        var rebootedProtection = new StartupProtectionRuntime(_vault, _root, _clock, rebooted);
        await rebooted.InitializeAsync(
            wrappedKeysPending: rebootedProtection.HoldsWrappedKeys,
            CancellationToken.None);
        Assert.True(rebooted.AwaitingUnlock);
        Assert.True(await rebootedProtection.TryUnlockAsync("4812", CancellationToken.None));
        Assert.True(rebooted.IsEnabled);
    }

    [Fact]
    public async Task A_sensor_verdict_cannot_release_sealed_keys()
    {
        var databasePath = Path.Combine(_root, "ghostshell.db");
        var (encryption, database) = ComposeEncryption(databasePath);
        await WriteProbeRowAsync(database);
        Assert.Null(await encryption.SetEnabledAsync(true, CancellationToken.None));
        var protection = new StartupProtectionRuntime(_vault, _root, _clock, encryption);
        Assert.Null(await protection.EnableAsync("4812", CancellationToken.None));

        var (rebooted, _) = ComposeEncryption(databasePath);
        var rebootedProtection = new StartupProtectionRuntime(_vault, _root, _clock, rebooted);
        await rebooted.InitializeAsync(
            wrappedKeysPending: true,
            CancellationToken.None);
        Assert.True(rebooted.AwaitingUnlock);

        // Touch ID passed — and must change nothing: a sensor verdict cannot
        // derive the wrapping key, and a lifted curtain over an unopenable
        // database would be a broken app, not an unlocked one.
        rebootedProtection.UnlockAuthenticated();

        Assert.True(rebootedProtection.IsLocked);
        Assert.True(rebooted.AwaitingUnlock);
    }

    private readonly List<GhostShellDatabase> _databases = [];

    private (ApplicationEncryptionRuntime Runtime, GhostShellDatabase Database) ComposeEncryption(
        string databasePath)
    {
        ApplicationEncryptionRuntime? runtime = null;
        var options = new SqliteStorageOptions(databasePath, acquireProfileLock: false)
        {
            PasswordProvider = () => runtime!.ConfigDatabasePassword,
        };
        var database = new GhostShellDatabase(options, TimeProvider.System);
        _databases.Add(database);
        runtime = new ApplicationEncryptionRuntime(_vault, databasePath, () => database);
        return (runtime, database);
    }

    private static async Task WriteProbeRowAsync(GhostShellDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO definitions(
                kind, id, schema_version, revision, name, payload_json,
                created_utc, updated_utc)
            VALUES (
                'probe', 'wrapped', 1, 1, 'probe', '{}',
                '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadProbeRowAsync(GhostShellDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM definitions WHERE id = 'wrapped';";
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The in-memory vault presenting as OS-protected.</summary>
    private sealed class PersistentVault : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();

        public string? RefusedDeleteReference { get; set; }

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
            request.Reference.Value == RefusedDeleteReference
                ? ValueTask.FromResult(SecretVaultResult<Unit>.Fail(
                    SecretVaultError.Create(SecretVaultErrorCode.AccessDenied)))
                : _inner.DeleteAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
            GetSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            _inner.GetMetadataAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
            ListSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            _inner.ListMetadataAsync(request, cancellationToken);

        public void Dispose() => _inner.Dispose();
    }
}
