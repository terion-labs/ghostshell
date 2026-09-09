using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteDatabaseTests
{
    [Fact]
    public async Task InitializationEnablesWalForeignKeysAndCurrentMigration()
    {
        await using var temporary = TemporaryDatabase.Create();

        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using var connection = await temporary.Database.OpenConnectionAsync(CancellationToken.None);

        Assert.False(new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling);
        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal("1", await ScalarAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(
            SqliteSchema.Migrations[^1].Version.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
        // Pin the executable provider, not merely the managed package graph,
        // so a different dylib cannot slip into a packaged application through
        // transitive restore or RID selection.
        var sqliteVersion = Version.Parse(
            await ScalarAsync(connection, "SELECT sqlite_version();"));
        var sqlite3McVersion = await ScalarAsync(
            connection,
            "SELECT sqlite3mc_version();");
        Assert.True(
            sqliteVersion >= new Version(3, 53, 4),
            $"Expected SQLite 3.53.4 or newer, found {sqliteVersion}.");
        Assert.Equal("SQLite3 Multiple Ciphers 2.4.0", sqlite3McVersion);
    }

    [Fact]
    public async Task MigrationChecksumDriftStopsStartup()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using (var connection = await temporary.Database.OpenConnectionAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum = 'changed' WHERE version = 1;";
            await command.ExecuteNonQueryAsync();
        }

        await temporary.ReopenAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await temporary.Database.EnsureInitializedAsync(CancellationToken.None));
        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatabaseFromANewerApplicationVersionStopsStartup()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        var futureVersion = 0;
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            futureVersion = int.Parse(
                await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"),
                System.Globalization.CultureInfo.InvariantCulture) + 1;
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc)
                VALUES ($version, 'future-schema', 'future-checksum', $appliedUtc);
                """;
            command.Parameters.AddWithValue("$version", futureVersion);
            command.Parameters.AddWithValue(
                "$appliedUtc",
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }

        await temporary.ReopenAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await temporary.Database.EnsureInitializedAsync(CancellationToken.None));
        Assert.Contains(
            $"unsupported schema version {futureVersion}",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyDefinitionPayloadsUpgradeWithoutLosingUserConfiguration()
    {
        await using var temporary = TemporaryDatabase.Create();
        var payloadMigration = SqliteSchema.Migrations.Single(migration =>
            string.Equals(
                migration.Name,
                "migrate-durable-definition-payloads",
                StringComparison.Ordinal));
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            payloadMigration.Version - 1);
        await using (var legacy = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            await InsertLegacyDefinitionAsync(
                legacy,
                DefinitionKind.Theme,
                "theme.legacy",
                "Legacy theme",
                """
                {"id":{"value":"theme.legacy"},"schemaVersion":1,"name":"Legacy theme","appearance":"Dark","platformProfile":"Custom","accent":{"kind":"AsuraBronze","customColor":null},"textScaleOverride":1.25}
                """);
            await InsertLegacyDefinitionAsync(
                legacy,
                DefinitionKind.QuickTerminalSettings,
                "quick-terminal.legacy",
                "Legacy Quick Terminal",
                """
                {"id":{"value":"quick-terminal.legacy"},"schemaVersion":1,"name":"Legacy Quick Terminal","hotkey":{"key":"GRAVE","modifiers":"Meta"},"monitorPolicy":"MainWindow","heightFraction":0.6,"opacity":0.7,"blurRadius":0,"animateSlide":true,"animationDurationMilliseconds":180,"reduceMotion":false,"restoreLastSession":true,"hideOnFocusLoss":true}
                """);
            await InsertLegacyDefinitionAsync(
                legacy,
                DefinitionKind.AiProviderProfile,
                "ai.legacy",
                "Legacy OpenAI",
                """
                {"id":{"value":"ai.legacy"},"schemaVersion":1,"name":"Legacy OpenAI","providerKind":"OpenAi","endpoint":"https://api.openai.com/v1/","authentication":{"$type":"api-key","secret":{"value":"vault-legacy-ai"}},"defaultModel":"gpt-legacy","order":3,"isEnabled":true}
                """);
            await InsertLegacyDefinitionAsync(
                legacy,
                DefinitionKind.AiProviderProfile,
                "ai.anthropic.legacy",
                "Legacy Anthropic",
                """
                {"id":{"value":"ai.anthropic.legacy"},"schemaVersion":1,"name":"Legacy Anthropic","providerKind":"Anthropic","endpoint":"https://api.anthropic.com/v1/","authentication":{"$type":"api-key","secret":{"value":"vault-legacy-anthropic"}},"defaultModel":"claude-legacy","order":4,"isEnabled":true}
                """);
            await InsertLegacyDefinitionAsync(
                legacy,
                DefinitionKind.AiProviderProfile,
                "ai.compatible.legacy",
                "Legacy compatible provider",
                """
                {"id":{"value":"ai.compatible.legacy"},"schemaVersion":1,"name":"Legacy compatible provider","providerKind":"OpenAiCompatible","endpoint":"http://localhost:11434/v1/","authentication":{"$type":"api-key","secret":{"value":"vault-legacy-compatible"}},"defaultModel":"local-legacy","order":5,"isEnabled":true}
                """);
            await InsertLegacyDefinitionAsync(
                legacy,
                DefinitionKind.McpServerProfile,
                "mcp.legacy",
                "Legacy MCP",
                """
                {"id":{"value":"mcp.legacy"},"schemaVersion":1,"name":"Legacy MCP","executable":"/opt/mcp/server","arguments":["--stdio"],"workingDirectory":"/srv/mcp","environment":[{"name":"TOKEN","reference":{"value":"vault-legacy-mcp"}}],"enabledTools":["status.read"],"isEnabled":false}
                """);
        }

        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);

        var theme = await new SqliteDefinitionRepository<ThemePreference>(
            temporary.Database,
            TimeProvider.System).GetAsync(
                new(DefinitionKind.Theme, "theme.legacy"),
                CancellationToken.None);
        Assert.True(theme.IsSuccess, theme.Error?.Message);
        Assert.Equal(1.25, theme.Value!.Value.TextScaleOverride);
        Assert.True(theme.Value.Value.IsTranslucent);

        var quickTerminal = await new SqliteDefinitionRepository<QuickTerminalSettings>(
            temporary.Database,
            TimeProvider.System).GetAsync(
                new(DefinitionKind.QuickTerminalSettings, "quick-terminal.legacy"),
                CancellationToken.None);
        Assert.True(quickTerminal.IsSuccess, quickTerminal.Error?.Message);
        Assert.False(quickTerminal.Value!.Value.IsTranslucent);
        Assert.True(quickTerminal.Value.Value.RestoreOnStart);

        var aiProvider = await new SqliteDefinitionRepository<AiProviderProfile>(
            temporary.Database,
            TimeProvider.System).GetAsync(
                new(DefinitionKind.AiProviderProfile, "ai.legacy"),
                CancellationToken.None);
        Assert.True(aiProvider.IsSuccess, aiProvider.Error?.Message);
        Assert.Equal(AiProviderProtocol.OpenAiResponses, aiProvider.Value!.Value.Protocol);
        Assert.Equal(AiProviderCapabilities.Responses, aiProvider.Value.Value.Capabilities);
        Assert.Equal("gpt-legacy", aiProvider.Value.Value.DefaultModel);

        var anthropic = await new SqliteDefinitionRepository<AiProviderProfile>(
            temporary.Database,
            TimeProvider.System).GetAsync(
                new(DefinitionKind.AiProviderProfile, "ai.anthropic.legacy"),
                CancellationToken.None);
        Assert.True(anthropic.IsSuccess, anthropic.Error?.Message);
        Assert.Equal(
            AiProviderProtocol.AnthropicMessages,
            anthropic.Value!.Value.Protocol);
        Assert.Equal(
            AiProviderCatalog.Get(AiProviderKind.Anthropic).Capabilities,
            anthropic.Value.Value.Capabilities);

        var compatible = await new SqliteDefinitionRepository<AiProviderProfile>(
            temporary.Database,
            TimeProvider.System).GetAsync(
                new(DefinitionKind.AiProviderProfile, "ai.compatible.legacy"),
                CancellationToken.None);
        Assert.True(compatible.IsSuccess, compatible.Error?.Message);
        Assert.Equal(
            AiProviderProtocol.OpenAiChatCompletions,
            compatible.Value!.Value.Protocol);
        Assert.Equal(
            AiProviderCapabilities.ChatCompletions,
            compatible.Value.Value.Capabilities);

        var mcpServer = await new SqliteDefinitionRepository<McpServerProfile>(
            temporary.Database,
            TimeProvider.System).GetAsync(
                new(DefinitionKind.McpServerProfile, "mcp.legacy"),
                CancellationToken.None);
        Assert.True(mcpServer.IsSuccess, mcpServer.Error?.Message);
        var stdio = Assert.IsType<McpServerTransport.Stdio>(mcpServer.Value!.Value.Transport);
        Assert.Equal("/opt/mcp/server", stdio.Executable);
        Assert.Equal(["--stdio"], stdio.Arguments);
        Assert.Equal(
            new SecretRef("vault-legacy-mcp"),
            Assert.Single(stdio.Environment).Reference);
        Assert.False(mcpServer.Value.Value.IsEnabled);
        Assert.True(mcpServer.Value.Value.IsTrusted);

        await using var migrated = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        Assert.Equal(
            "6",
            await ScalarAsync(
                migrated,
                """
                SELECT COUNT(*)
                FROM definitions
                WHERE kind IN (
                    'theme',
                    'quick-terminal-settings',
                    'ai-provider-profile',
                    'mcp-server-profile')
                    AND schema_version = 2
                    AND json_extract(payload_json, '$.schemaVersion') = 2
                    AND revision = 7;
                """));
        Assert.Equal(
            "6",
            await ScalarAsync(
                migrated,
                """
                SELECT COUNT(*)
                FROM definitions
                WHERE id LIKE '%.legacy'
                    AND updated_utc = '2026-07-23T08:30:00.0000000+00:00';
                """));
        Assert.Equal(
            "object",
            await ScalarAsync(
                migrated,
                """
                SELECT json_type(payload_json, '$.transport')
                FROM definitions
                WHERE kind = 'mcp-server-profile' AND id = 'mcp.legacy';
                """));
        Assert.Equal(
            "4",
            await ScalarAsync(
                migrated,
                """
                SELECT
                    (SELECT json_type(payload_json, '$.cornerRadiusOverride') IS NULL
                     FROM definitions WHERE id = 'theme.legacy')
                    + (SELECT json_type(payload_json, '$.backdropBlurRadius') IS NULL
                       FROM definitions WHERE id = 'theme.legacy')
                    + (SELECT json_type(payload_json, '$.blurRadius') IS NULL
                       FROM definitions WHERE id = 'quick-terminal.legacy')
                    + (SELECT json_type(payload_json, '$.executable') IS NULL
                       FROM definitions WHERE id = 'mcp.legacy');
                """));
        Assert.Single(Directory.GetFiles(
            new SqliteStorageOptions(temporary.DatabasePath).BackupDirectory,
            $"asura-before-v{payloadMigration.Version}-*.db"));
    }

    [Fact]
    public async Task UnknownLegacyDefinitionVersionRollsBackAndCanRetry()
    {
        await using var temporary = TemporaryDatabase.Create();
        var payloadMigration = SqliteSchema.Migrations.Single(migration =>
            string.Equals(
                migration.Name,
                "migrate-durable-definition-payloads",
                StringComparison.Ordinal));
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            payloadMigration.Version - 1);
        await using (var legacy = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            await InsertLegacyDefinitionAsync(
                legacy,
                DefinitionKind.AiProviderProfile,
                "ai.unknown",
                "Unknown provider",
                """
                {"id":{"value":"ai.unknown"},"schemaVersion":1,"name":"Unknown provider","providerKind":"FutureProvider","endpoint":"https://example.test/v1/","authentication":{"$type":"api-key","secret":{"value":"vault-unknown"}},"defaultModel":"future","order":0,"isEnabled":true}
                """);
        }

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await temporary.Database.EnsureInitializedAsync(CancellationToken.None));

        await using (var unchanged = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            Assert.Equal(
                (payloadMigration.Version - 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(
                    unchanged,
                    "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "1|1|FutureProvider",
                await ScalarAsync(
                    unchanged,
                    """
                    SELECT schema_version
                        || '|'
                        || json_extract(payload_json, '$.schemaVersion')
                        || '|'
                        || json_extract(payload_json, '$.providerKind')
                    FROM definitions
                    WHERE kind = 'ai-provider-profile' AND id = 'ai.unknown';
                    """));
            await using var repair = unchanged.CreateCommand();
            repair.CommandText = """
                UPDATE definitions
                SET payload_json = json_set(
                    payload_json,
                    '$.providerKind',
                    'OpenAi')
                WHERE kind = 'ai-provider-profile' AND id = 'ai.unknown';
                """;
            await repair.ExecuteNonQueryAsync();
        }

        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using var retried = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        Assert.Equal(
            payloadMigration.Version.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(retried, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            "2|OpenAiResponses",
            await ScalarAsync(
                retried,
                """
                SELECT schema_version
                    || '|'
                    || json_extract(payload_json, '$.protocol')
                FROM definitions
                WHERE kind = 'ai-provider-profile' AND id = 'ai.unknown';
                """));
    }

    public static TheoryData<int> HistoricalSchemaVersions
    {
        get
        {
            var versions = new TheoryData<int>();
            foreach (var migration in SqliteSchema.Migrations.SkipLast(1))
            {
                versions.Add(migration.Version);
            }

            return versions;
        }
    }

    [Theory]
    [MemberData(nameof(HistoricalSchemaVersions))]
    public async Task HistoricalSchemasUpgradeWithoutLosingDurableOrRecoveryState(
        int sourceVersion)
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            sourceVersion);

        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                HistoricalDatabaseFixture.DefinitionName,
                await ScalarAsync(
                    connection,
                    $"""
                    SELECT name
                    FROM definitions
                    WHERE kind = 'layout'
                        AND id = '{HistoricalDatabaseFixture.DefinitionId}';
                    """));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    connection,
                    $"""
                    SELECT COUNT(*)
                    FROM audit_events
                    WHERE event_id = '{HistoricalDatabaseFixture.AuditEventId}';
                    """));
            Assert.Equal(
                "2",
                await ScalarAsync(connection, "SELECT COUNT(*) FROM runtime_snapshots;"));
            Assert.Equal(
                sourceVersion >= 2 ? "1" : "0",
                await ScalarAsync(
                    connection,
                    $"""
                    SELECT COUNT(*)
                    FROM recent_sessions
                    WHERE session_id = '{HistoricalDatabaseFixture.RecentSessionId}';
                    """));
            // Upgrading turns session history off wherever it was never
            // deliberately chosen: retaining a record of every session opened is
            // opt-in, and an upgrade is not the user opting in.
            Assert.Equal(
                "0",
                await ScalarAsync(
                    connection,
                    """
                    SELECT maximum_entries
                    FROM recent_session_retention
                    WHERE singleton_id = 1;
                    """));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    connection,
                    """
                    SELECT completed_version
                    FROM onboarding_progress
                    WHERE singleton_id = 1;
                    """));
            await AssertDatabaseIntegrityAsync(connection);
        }

        var catalog = CreateDefinitionCatalog(temporary.Database);
        var catalogResult = await catalog.InitializeAsync(CancellationToken.None);
        Assert.True(catalogResult.IsSuccess, catalogResult.Error?.Message);
        var restoredLayout = Assert.Single(
            catalogResult.Value!.Layouts,
            item => string.Equals(item.Value.Id.Value, HistoricalDatabaseFixture.DefinitionId, StringComparison.Ordinal));
        Assert.Equal(HistoricalDatabaseFixture.DefinitionName, restoredLayout.Value.Name);

        var runStore = new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System);
        var currentRun = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True(currentRun.RecoveryRequired);
        Assert.Equal(
            HistoricalDatabaseFixture.InterruptedRunId,
            currentRun.PreviousState.RunId);

        var startupState = new ApplicationStartupState();
        startupState.Initialize(currentRun);
        var recoveryStore = new SqliteRuntimeRecoveryStore(temporary.Database, startupState);
        var restored = Success(await recoveryStore.LoadAsync(
            HistoricalDatabaseFixture.InterruptedRunId,
            CancellationToken.None));

        var snapshot = Assert.Single(restored);
        Assert.Equal(HistoricalDatabaseFixture.InterruptedRunId, snapshot.RunId);
        Assert.Equal(HistoricalDatabaseFixture.InterruptedSnapshotKey, snapshot.Key);
        Assert.Single(Success(await recoveryStore.LoadAsync(
            HistoricalDatabaseFixture.OtherRunId,
            CancellationToken.None)));
    }

    [Theory]
    [MemberData(nameof(HistoricalSchemaVersions))]
    public async Task FailedHistoricalUpgradeRollsBackAndCanRetry(
        int sourceVersion)
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            sourceVersion);
        await HistoricalDatabaseFixture.AddNextMigrationCollisionAsync(
            temporary.DatabasePath,
            sourceVersion);

        // Most migrations are obstructed by pre-creating the object they create;
        // one that only rewrites a row is obstructed by refusing the write. What
        // matters either way is that it failed and left the schema where it was.
        var error = await Assert.ThrowsAsync<SqliteException>(async () =>
            await temporary.Database.EnsureInitializedAsync(CancellationToken.None));
        Assert.Contains(
            sourceVersion switch
            {
                8 => "next migration collision",
                13 or 16 => "duplicate column name",
                _ => "already exists",
            },
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        await using (var unchanged = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            Assert.Equal(
                sourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(
                    unchanged,
                    "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    unchanged,
                    $"""
                    SELECT COUNT(*)
                    FROM definitions
                    WHERE id = '{HistoricalDatabaseFixture.DefinitionId}';
                    """));
            Assert.Equal(
                "2",
                await ScalarAsync(unchanged, "SELECT COUNT(*) FROM runtime_snapshots;"));
            await AssertDatabaseIntegrityAsync(unchanged);
        }

        await HistoricalDatabaseFixture.RemoveNextMigrationCollisionAsync(
            temporary.DatabasePath,
            sourceVersion);
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using var migrated = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        Assert.Equal(
            SqliteSchema.Migrations[^1].Version.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;"));
        await AssertDatabaseIntegrityAsync(migrated);
    }

    [Fact]
    public async Task FailedMigrationRollsBackEarlierStatementsAndCanRetry()
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            SqliteSchema.Migrations[^1].Version);
        var migration = new SqliteMigration(
            SqliteSchema.Migrations[^1].Version + 1,
            "transaction-rollback-probe",
            """
            CREATE TABLE migration_probe (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1)
            );

            INSERT INTO migration_probe(singleton_id) VALUES (1);
            INSERT INTO migration_gate(value) VALUES ('opened');
            """);
        await using var database = CreateDatabase(temporary.DatabasePath, migration);

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await database.EnsureInitializedAsync(CancellationToken.None));

        await using (var unchanged = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(unchanged, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "0",
                await ScalarAsync(
                    unchanged,
                    """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'migration_probe';
                    """));
            await using var gate = unchanged.CreateCommand();
            gate.CommandText = "CREATE TABLE migration_gate (value TEXT NOT NULL);";
            await gate.ExecuteNonQueryAsync();
        }

        await database.EnsureInitializedAsync(CancellationToken.None);
        await using var migrated = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            migration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            "1",
            await ScalarAsync(migrated, "SELECT COUNT(*) FROM migration_probe;"));
        Assert.Equal(
            "opened",
            await ScalarAsync(migrated, "SELECT value FROM migration_gate;"));
        await AssertDatabaseIntegrityAsync(migrated);
    }

    [Fact]
    public async Task DestructiveMigrationCreatesAValidatedRestorableBackup()
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            SqliteSchema.Migrations[^1].Version);
        var migration = DestructiveProbeMigration();
        var options = new SqliteStorageOptions(temporary.DatabasePath);
        await using var database = new AsuraDatabase(
            options,
            TimeProvider.System,
            [.. SqliteSchema.Migrations, migration]);

        await database.EnsureInitializedAsync(CancellationToken.None);

        var backupPath = Assert.Single(Directory.GetFiles(
            options.BackupDirectory,
            $"asura-before-v{migration.Version}-*.db"));
        var backupBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using (var backup = new SqliteConnection(backupBuilder.ConnectionString))
        {
            await backup.OpenAsync();
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(backup, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "0",
                await ScalarAsync(
                    backup,
                    """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'destructive_probe';
                    """));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    backup,
                    $"""
                    SELECT COUNT(*)
                    FROM definitions
                    WHERE id = '{HistoricalDatabaseFixture.DefinitionId}';
                    """));
            await AssertDatabaseIntegrityAsync(backup);
        }

        await using (var migrated = await database.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Equal(
                migration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "1",
                await ScalarAsync(migrated, "SELECT COUNT(*) FROM destructive_probe;"));
        }

        await database.DisposeAsync();
        await using var restoredDatabase = new AsuraDatabase(
            new SqliteStorageOptions(backupPath),
            TimeProvider.System);
        await restoredDatabase.EnsureInitializedAsync(CancellationToken.None);
        var restoredLayouts = new SqliteDefinitionRepository<LayoutDefinition>(
            restoredDatabase,
            TimeProvider.System);
        var restored = await restoredLayouts.GetAsync(
            new DefinitionKey(LayoutDefinition.Kind, HistoricalDatabaseFixture.DefinitionId),
            CancellationToken.None);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Equal(HistoricalDatabaseFixture.DefinitionName, restored.Value!.Value.Name);
    }

    [Fact]
    public async Task DestructiveMigrationKeepsAnEncryptedBackupRecoverableWithTheActiveKey()
    {
        const string password =
            "9e312769876efb817a04ba20fa8f20ceea3df65dba6714f195fe086f5aa8fc32";
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            SqliteSchema.Migrations[^1].Version);
        await EncryptDatabaseAsync(temporary.DatabasePath, password);
        var migration = DestructiveProbeMigration();
        var options = new SqliteStorageOptions(temporary.DatabasePath)
        {
            PasswordProvider = () => password,
        };
        await using var database = new AsuraDatabase(
            options,
            TimeProvider.System,
            [.. SqliteSchema.Migrations, migration]);

        await database.EnsureInitializedAsync(CancellationToken.None);

        var backupPath = Assert.Single(Directory.GetFiles(
            options.BackupDirectory,
            $"asura-before-v{migration.Version}-*.db"));
        var header = new byte[16];
        await using (var image = File.OpenRead(backupPath))
        {
            await image.ReadExactlyAsync(header);
        }

        Assert.False(
            string.Equals(
                "SQLite format 3\0",
                System.Text.Encoding.ASCII.GetString(header),
                StringComparison.Ordinal),
            "The encrypted backup still announces itself as plain SQLite.");
        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            var unkeyedBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            await using var unkeyed = new SqliteConnection(unkeyedBuilder.ConnectionString);
            await unkeyed.OpenAsync();
            await ScalarAsync(unkeyed, "SELECT COUNT(*) FROM schema_migrations;");
        });

        var keyedBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Password = password,
        };
        await using (var keyed = new SqliteConnection(keyedBuilder.ConnectionString))
        {
            await keyed.OpenAsync();
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(keyed, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    keyed,
                    $"SELECT COUNT(*) FROM definitions WHERE id = "
                    + $"'{HistoricalDatabaseFixture.DefinitionId}';"));
            await AssertDatabaseIntegrityAsync(keyed);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(options.BackupDirectory));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(backupPath));
        }

        await database.DisposeAsync();
        await using var restoredDatabase = new AsuraDatabase(
            new SqliteStorageOptions(backupPath)
            {
                PasswordProvider = () => password,
            },
            TimeProvider.System);
        await restoredDatabase.EnsureInitializedAsync(CancellationToken.None);
        var restoredLayouts = new SqliteDefinitionRepository<LayoutDefinition>(
            restoredDatabase,
            TimeProvider.System);
        var restored = await restoredLayouts.GetAsync(
            new DefinitionKey(LayoutDefinition.Kind, HistoricalDatabaseFixture.DefinitionId),
            CancellationToken.None);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Equal(HistoricalDatabaseFixture.DefinitionName, restored.Value!.Value.Name);
    }

    [Fact]
    public async Task BackupValidationFailurePublishesNothingAndCanRetry()
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            SqliteSchema.Migrations[^1].Version);
        await using (var invalid = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            await using var command = invalid.CreateCommand();
            command.CommandText = $"""
                PRAGMA foreign_keys = OFF;
                INSERT INTO definition_references(
                    owner_kind, owner_id, target_kind, target_id, role)
                VALUES (
                    'layout', 'missing-owner', 'layout',
                    '{HistoricalDatabaseFixture.DefinitionId}', 'fixture');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new SqliteStorageOptions(temporary.DatabasePath);
        var migration = DestructiveProbeMigration();
        await using var database = new AsuraDatabase(
            options,
            TimeProvider.System,
            [.. SqliteSchema.Migrations, migration]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await database.EnsureInitializedAsync(CancellationToken.None));
        Assert.Contains("foreign-key validation", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(options.BackupDirectory));

        await using (var unchanged = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(unchanged, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "0",
                await ScalarAsync(
                    unchanged,
                    """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'destructive_probe';
                    """));
            await using var repair = unchanged.CreateCommand();
            repair.CommandText = """
                DELETE FROM definition_references
                WHERE owner_kind = 'layout' AND owner_id = 'missing-owner';
                """;
            Assert.Equal(1, await repair.ExecuteNonQueryAsync());
        }

        await database.EnsureInitializedAsync(CancellationToken.None);

        Assert.Single(Directory.GetFiles(
            options.BackupDirectory,
            $"asura-before-v{migration.Version}-*.db"));
        Assert.DoesNotContain(
            Directory.GetFileSystemEntries(options.BackupDirectory),
            path => Path.GetFileName(path).StartsWith('.'));
        await using var migrated = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            migration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;"));
    }

    [Fact]
    public async Task BackupCreationFailureLeavesSourceUnchangedAndCanRetry()
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            SqliteSchema.Migrations[^1].Version);
        var options = new SqliteStorageOptions(temporary.DatabasePath);
        await File.WriteAllTextAsync(options.BackupDirectory, "not-a-directory");
        var migration = DestructiveProbeMigration();
        await using var database = new AsuraDatabase(
            options,
            TimeProvider.System,
            [.. SqliteSchema.Migrations, migration]);

        await Assert.ThrowsAsync<IOException>(async () =>
            await database.EnsureInitializedAsync(CancellationToken.None));

        await using (var unchanged = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(unchanged, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "0",
                await ScalarAsync(
                    unchanged,
                    """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'destructive_probe';
                    """));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    unchanged,
                    $"""
                    SELECT COUNT(*)
                    FROM definitions
                    WHERE id = '{HistoricalDatabaseFixture.DefinitionId}';
                    """));
            await AssertDatabaseIntegrityAsync(unchanged);
        }

        File.Delete(options.BackupDirectory);
        await database.EnsureInitializedAsync(CancellationToken.None);

        Assert.Single(Directory.GetFiles(
            options.BackupDirectory,
            $"asura-before-v{migration.Version}-*.db"));
        await using var migrated = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            migration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            "1",
            await ScalarAsync(migrated, "SELECT COUNT(*) FROM destructive_probe;"));
    }

    private static async Task<string> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task InsertLegacyDefinitionAsync(
        SqliteConnection connection,
        DefinitionKind kind,
        string id,
        string name,
        string payloadJson)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO definitions(
                kind,
                id,
                schema_version,
                revision,
                name,
                payload_json,
                created_utc,
                updated_utc)
            VALUES (
                $kind,
                $id,
                1,
                7,
                $name,
                $payloadJson,
                $timestamp,
                $timestamp);
            """;
        command.Parameters.AddWithValue("$kind", kind.Value);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue(
            "$timestamp",
            HistoricalDatabaseFixture.ReferenceTime.ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EncryptDatabaseAsync(string path, string password)
    {
        await using var connection = await HistoricalDatabaseFixture.OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode=DELETE;"
            + $"PRAGMA rekey='{password}';"
            + "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync();
    }

    private static AsuraDatabase CreateDatabase(
        string databasePath,
        SqliteMigration additionalMigration) =>
        new(
            new SqliteStorageOptions(databasePath),
            TimeProvider.System,
            [.. SqliteSchema.Migrations, additionalMigration]);

    private static DefinitionCatalog CreateDefinitionCatalog(AsuraDatabase database)
    {
        var timeProvider = TimeProvider.System;
        return new DefinitionCatalog(
            new SqliteDefinitionRepository<ConnectionProfile>(database, timeProvider),
            new SqliteDefinitionRepository<LayoutDefinition>(database, timeProvider),
            new SqliteDefinitionRepository<ScreenDefinition>(database, timeProvider),
            new SqliteDefinitionRepository<WorkspaceDefinition>(database, timeProvider),
            new SqliteDefinitionRepository<ThemePreference>(database, timeProvider),
            new SqliteDefinitionRepository<TerminalProfile>(database, timeProvider),
            new SqliteDefinitionRepository<KeymapProfile>(database, timeProvider),
            new SqliteDefinitionRepository<FileProviderProfile>(database, timeProvider),
            new SqliteDefinitionRepository<AiProviderProfile>(database, timeProvider),
            new SqliteDefinitionRepository<McpServerProfile>(database, timeProvider),
            new SqliteDefinitionRepository<QuickTerminalSettings>(database, timeProvider));
    }

    private static SqliteMigration DestructiveProbeMigration() => new(
        SqliteSchema.Migrations[^1].Version + 1,
        "destructive-backup-probe",
        """
        CREATE TABLE destructive_probe (
            singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1)
        );

        INSERT INTO destructive_probe(singleton_id) VALUES (1);
        """,
        IsDestructive: true);

    private static async Task AssertDatabaseIntegrityAsync(SqliteConnection connection)
    {
        Assert.Equal("ok", await ScalarAsync(connection, "PRAGMA integrity_check;"));
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    private static T Success<T>(ApplicationRunResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }
}
