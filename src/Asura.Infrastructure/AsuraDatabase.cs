using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

public sealed class AsuraDatabase : IAsyncDisposable
{
    private const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// The engine version this build bundles, asserted so a stray system
    /// library can never be swapped in underneath. The bundled SQLite3
    /// Multiple Ciphers build currently tracks SQLite 3.53.4.
    /// </summary>
    private static readonly Version MinimumSqliteVersion = new(3, 53, 4);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly IReadOnlyList<SqliteMigration> _migrations;
    private readonly SqliteStorageOptions _options;
    private readonly TimeProvider _timeProvider;
    private ProfileDatabaseLock? _profileLock;
    private bool _initialized;
    private bool _disposed;

    public AsuraDatabase(SqliteStorageOptions options, TimeProvider timeProvider)
        : this(options, timeProvider, SqliteSchema.Migrations)
    {
    }

    internal AsuraDatabase(
        SqliteStorageOptions options,
        TimeProvider timeProvider,
        IReadOnlyList<SqliteMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(migrations);
        var migrationCatalog = migrations.ToArray();
        if (migrationCatalog.Length == 0)
        {
            throw new ArgumentException(
                "At least one SQLite migration is required.",
                nameof(migrations));
        }

        for (var index = 0; index < migrationCatalog.Length; index++)
        {
            var migration = migrationCatalog[index];
            var expectedVersion = index + 1;
            if (migration.Version != expectedVersion
                || string.IsNullOrWhiteSpace(migration.Name)
                || string.IsNullOrWhiteSpace(migration.Sql))
            {
                throw new ArgumentException(
                    "SQLite migrations must be non-empty and ordered contiguously from version one.",
                    nameof(migrations));
            }
        }

        _options = options;
        _timeProvider = timeProvider;
        _migrations = migrationCatalog;
    }

    public async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_options.DatabasePath)!;
            Directory.CreateDirectory(directory);
            if (_options.AcquireProfileLock)
            {
                _profileLock = ProfileDatabaseLock.Acquire(_options.DatabasePath);
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await AssertSafeSqliteVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        catch
        {
            _profileLock?.Dispose();
            _profileLock = null;
            throw;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _initializationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _profileLock?.Dispose();
            _profileLock = null;
        }
        finally
        {
            _initializationGate.Release();
            _initializationGate.Dispose();
        }
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // The configuration database is a correctness boundary, not a
            // throughput database. A logical store operation exclusively owns
            // its native sqlite3 connection from open through disposal. Native
            // connection pooling allows a connection used by one store (for
            // example checkpoint persistence) to be handed to an unrelated
            // concurrent agent-audit operation. Repeated macOS/arm64 crash
            // reports showed the reused connection's per-connection lookaside
            // free list was corrupt during prepare. Do not reuse that native
            // state across application subsystems.
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = checked((int)Math.Ceiling(_options.BusyTimeout.TotalSeconds)),
        };
        if (_options.PasswordProvider?.Invoke() is { } password)
        {
            builder.Password = password;
        }

        return new SqliteConnection(builder.ConnectionString);
    }

    /// <summary>
    /// Runs an operation that must have the database file to itself — turning
    /// encryption on or off rewrites every page. New opens wait at the gate
    /// and then re-verify the (possibly re-keyed) file.
    /// </summary>
    public async ValueTask RunExclusiveMaintenanceAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _initialized = false;
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
        var mode = Convert.ToString(
            await journalCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SQLite could not enable WAL mode.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = {checked((int)_options.BusyTimeout.TotalMilliseconds)};
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertSafeSqliteVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var value = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (!Version.TryParse(value, out var version) || version < MinimumSqliteVersion)
        {
            throw new InvalidOperationException(
                $"Asura requires SQLite {MinimumSqliteVersion} or newer for safe WAL recovery.");
        }
    }

    private async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var bootstrap = connection.CreateCommand())
        {
            bootstrap.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    checksum TEXT NOT NULL,
                    applied_utc TEXT NOT NULL
                );
                """;
            await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var latestSupportedVersion = _migrations[^1].Version;
        var futureVersion = applied.Keys
            .Where(version => version > latestSupportedVersion)
            .Order()
            .FirstOrDefault();
        if (futureVersion > 0)
        {
            throw new InvalidOperationException(
                $"This database was upgraded to unsupported schema version {futureVersion}.");
        }

        foreach (var migration in _migrations)
        {
            var checksum = ComputeChecksum(migration.Sql);
            if (applied.TryGetValue(migration.Version, out var existing))
            {
                if (!string.Equals(existing.Checksum, checksum, StringComparison.Ordinal)
                    || !string.Equals(existing.Name, migration.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"SQLite migration {migration.Version} does not match its recorded checksum.");
                }

                continue;
            }

            if (migration.IsDestructive)
            {
                await CreateValidatedBackupAsync(connection, migration.Version, cancellationToken)
                    .ConfigureAwait(false);
            }

            await using var transaction = connection.BeginTransaction();
            try
            {
                await using var migrationCommand = connection.CreateCommand();
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = migration.Sql;
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using var recordCommand = connection.CreateCommand();
                recordCommand.Transaction = transaction;
                recordCommand.CommandText = """
                    INSERT INTO schema_migrations(version, name, checksum, applied_utc)
                    VALUES ($version, $name, $checksum, $appliedUtc);
                    """;
                recordCommand.Parameters.AddWithValue("$version", migration.Version);
                recordCommand.Parameters.AddWithValue("$name", migration.Name);
                recordCommand.Parameters.AddWithValue("$checksum", checksum);
                recordCommand.Parameters.AddWithValue(
                    "$appliedUtc",
                    _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
                await recordCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task<Dictionary<int, AppliedMigration>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, AppliedMigration>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name, checksum FROM schema_migrations ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(
                reader.GetInt32(0),
                new AppliedMigration(reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    private async Task CreateValidatedBackupAsync(
        SqliteConnection source,
        int nextVersion,
        CancellationToken cancellationToken)
    {
        EnsureOwnerOnlyBackupDirectory();
        var timestamp = _timeProvider.GetUtcNow().ToString(
            "yyyyMMddTHHmmssfffZ",
            CultureInfo.InvariantCulture);
        var uniqueSuffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var fileStem = $"asura-before-v{nextVersion}-{timestamp}-{uniqueSuffix}";
        var backupPath = Path.Combine(_options.BackupDirectory, $"{fileStem}.db");
        var temporaryPath = Path.Combine(_options.BackupDirectory, $".{fileStem}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateOwnerOnlyFile(temporaryPath);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };
            if (_options.PasswordProvider?.Invoke() is { } password)
            {
                // SQLite online backup applies the destination connection's
                // codec. Match the source's active application key so the
                // backup cannot silently cross into plaintext storage.
                builder.Password = password;
            }

            await using (var destination = new SqliteConnection(builder.ConnectionString))
            {
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
                cancellationToken.ThrowIfCancellationRequested();
                await ValidateBackupAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, backupPath, overwrite: false);
            EnsureOwnerOnlyFileMode(backupPath);
        }
        catch (Exception backupFailure)
        {
            var cleanupFailures = DeleteBackupArtifacts(temporaryPath);
            if (cleanupFailures.Count != 0)
            {
                var failures = new List<Exception>(cleanupFailures.Count + 1)
                {
                    backupFailure,
                };
                failures.AddRange(cleanupFailures);
                throw new InvalidOperationException(
                    "SQLite migration backup failed and cleanup of its unvalidated "
                    + "temporary artifacts could not be confirmed.",
                    new AggregateException(failures));
            }

            throw;
        }
    }

    private void EnsureOwnerOnlyBackupDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_options.BackupDirectory);
        }
        else
        {
            Directory.CreateDirectory(_options.BackupDirectory, OwnerDirectoryMode);
            File.SetUnixFileMode(_options.BackupDirectory, OwnerDirectoryMode);
        }
    }

    private static void CreateOwnerOnlyFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFileMode;
        }

        using var _ = new FileStream(path, options);
    }

    private static void EnsureOwnerOnlyFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnerFileMode);
        }
    }

    private static async Task ValidateBackupAsync(
        SqliteConnection destination,
        CancellationToken cancellationToken)
    {
        await using (var integrityCommand = destination.CreateCommand())
        {
            integrityCommand.CommandText = "PRAGMA integrity_check;";
            var integrity = Convert.ToString(
                await integrityCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "SQLite refused a destructive migration because its backup "
                    + "failed integrity validation.");
            }
        }

        await using var foreignKeyCommand = destination.CreateCommand();
        foreignKeyCommand.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeyCommand.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "SQLite refused a destructive migration because its backup "
                + "failed foreign-key validation.");
        }
    }

    private static IReadOnlyList<Exception> DeleteBackupArtifacts(string temporaryPath)
    {
        var failures = new List<Exception>();
        foreach (var path in new[]
                 {
                     temporaryPath,
                     $"{temporaryPath}-journal",
                     $"{temporaryPath}-shm",
                     $"{temporaryPath}-wal",
                 })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private static string ComputeChecksum(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record AppliedMigration(string Name, string Checksum);
}
