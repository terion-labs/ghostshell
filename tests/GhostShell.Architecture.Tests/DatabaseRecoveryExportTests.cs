using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Databases;
using GhostShell.Infrastructure;
using GhostShell.Redis;

namespace GhostShell.Architecture.Tests;

public sealed class DatabaseRecoveryExportTests
{
    [Theory]
    [InlineData("sqlite", "Data Source=/nonexistent/fixture.db", false)]
    [InlineData("sqlite", "Data Source=/nonexistent/fixture.db;Password=export-private-fixture", true)]
    [InlineData("postgres", "Host=fixture.invalid;SSL Mode=Require", false)]
    [InlineData("postgres", "Host=fixture.invalid;SSL Password=export-private-fixture", true)]
    [InlineData("postgres", "postgresql://user:export-private-fixture@fixture.invalid/app", true)]
    [InlineData("postgres", "postgresql://fixture.invalid/app?sslpassword=export-private-fixture", true)]
    [InlineData("postgres", "postgresql://fixture.invalid/app?%73slpassword=export-private-fixture", true)]
    [InlineData("mysql", "Server=fixture.invalid;SslMode=Required", false)]
    [InlineData("mysql", "mysql://user:export-private-fixture@fixture.invalid/app", true)]
    [InlineData("mariadb", "Server=fixture.invalid;Password=export-private-fixture", true)]
    [InlineData("mariadb", "Server=fixture.invalid", false)]
    [InlineData("sqlserver", "Server=fixture.invalid;Integrated Security=true", false)]
    [InlineData("sqlserver", "Server=fixture.invalid;Pwd=export-private-fixture", true)]
    [InlineData("cockroach", "Host=fixture.invalid;Password=export-private-fixture", true)]
    [InlineData("cockroach", "Host=fixture.invalid;SSL Mode=Require", false)]
    [InlineData("redshift", "Host=fixture.invalid;Password=export-private-fixture", true)]
    [InlineData("redshift", "Host=fixture.invalid;SSL Mode=Require", false)]
    [InlineData("duckdb", "Data Source=/nonexistent/fixture.duckdb", false)]
    [InlineData("oracle", "Data Source=fixture.invalid/service;Password=export-private-fixture", true)]
    [InlineData("oracle", "Data Source=fixture.invalid/service;Pooling=false", false)]
    [InlineData("firebird", "DataSource=fixture.invalid;Database=app;Password=export-private-fixture", true)]
    [InlineData("firebird", "DataSource=fixture.invalid;Database=app", false)]
    [InlineData("clickhouse", "Host=fixture.invalid;Password=export-private-fixture", true)]
    [InlineData("clickhouse", "Host=fixture.invalid;Database=app", false)]
    [InlineData("redis", "fixture.invalid:6379,password=export-private-fixture", true)]
    [InlineData("redis", "rediss://user:export-private-fixture@fixture.invalid/1", true)]
    [InlineData("redis", "fixture.invalid:6379,ssl=true", false)]
    public async Task Never_opened_legacy_targets_are_sanitized_only_in_export(string driver, string target, bool confidential)
    {
        var directory = Directory.CreateTempSubdirectory("ghostshell-database-export-").FullName;
        try
        {
            await using var database = new GhostShellDatabase(new(Path.Combine(directory, "test.db")), TimeProvider.System);
            await using var relational = new DatabasePanelClient();
            var catalog = new RedisConnectionCatalog(relational, new ForbiddenRedisSessions());
            var store = new SqliteDefinitionBundleStore(database, TimeProvider.System, catalog);
            var layout = new LayoutDefinition(new("layout"), 1, "Layout", new(1, 1),
                [new(new("main"), new(0, 0, 1, 1), new(160, 100))]);
            Assert.True((await new SqliteDefinitionRepository<LayoutDefinition>(database, TimeProvider.System)
                .SaveAsync(layout, null, CancellationToken.None)).IsSuccess);
            var location = new DatabasePanelTarget(driver, target).Serialize();
            var screen = new ScreenDefinition(new("legacy"), 1, "Legacy", null, layout.Id,
                [new(new("panel"), new("main"), ScreenPanelKind.DatabaseViewer, "Database", null, new(location))]);
            Assert.True((await new SqliteDefinitionRepository<ScreenDefinition>(database, TimeProvider.System)
                .SaveAsync(screen, null, CancellationToken.None)).IsSuccess);
            var before = await ReadPayloadAsync(database);
            var result = await store.ExportAsync(CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error?.Message);
            var bundle = result.Value!;
            Assert.Equal(confidential ? 1 : 0, bundle.ReconnectRequiredDatabasePanelCount);
            var document = Assert.Single(bundle.Definitions, document => document.Kind == ScreenDefinition.Kind);
            using var json = JsonDocument.Parse(document.PayloadJson);
            Assert.Equal(confidential ? DatabaseRecoveryToken.ReconnectTarget : location,
                json.RootElement.GetProperty("panels")[0].GetProperty("startup").GetProperty("location").GetString());
            if (confidential)
            {
                Assert.All(bundle.Definitions, document => Assert.DoesNotContain("export-private-fixture", document.PayloadJson, StringComparison.Ordinal));
            }
            Assert.Equal(before, await ReadPayloadAsync(database));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown", "Host=fixture.invalid;Password=export-private-fixture")]
    [InlineData("postgres", "Host='export-private-fixture")]
    [InlineData("postgres", "postgresql://fixture.invalid/app?unknown_token=export-private-fixture")]
    [InlineData("redis", "redis://fixture.invalid/0?token=export-private-fixture")]
    public async Task Unknown_options_fail_export_without_disclosing_target(string driver, string target)
    {
        var directory = Directory.CreateTempSubdirectory("ghostshell-database-export-invalid-").FullName;
        try
        {
            await using var database = new GhostShellDatabase(new(Path.Combine(directory, "test.db")), TimeProvider.System);
            await using var relational = new DatabasePanelClient();
            var store = new SqliteDefinitionBundleStore(database, TimeProvider.System,
                new RedisConnectionCatalog(relational, new ForbiddenRedisSessions()));
            var layout = new LayoutDefinition(new("layout"), 1, "Layout", new(1, 1),
                [new(new("main"), new(0, 0, 1, 1), new(160, 100))]);
            Assert.True((await new SqliteDefinitionRepository<LayoutDefinition>(database, TimeProvider.System)
                .SaveAsync(layout, null, CancellationToken.None)).IsSuccess);
            var screen = new ScreenDefinition(new("legacy"), 1, "Legacy", null, layout.Id,
                [new(new("panel"), new("main"), ScreenPanelKind.DatabaseViewer, "Database", null,
                    new(new DatabasePanelTarget(driver, target).Serialize()))]);
            Assert.True((await new SqliteDefinitionRepository<ScreenDefinition>(database, TimeProvider.System)
                .SaveAsync(screen, null, CancellationToken.None)).IsSuccess);
            var before = await ReadPayloadAsync(database);
            var result = await store.ExportAsync(CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.DoesNotContain("export-private-fixture", result.Error!.Message, StringComparison.Ordinal);
            Assert.Equal(before, await ReadPayloadAsync(database));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> ReadPayloadAsync(GhostShellDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM definitions WHERE id = 'legacy';";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ForbiddenRedisSessions : IRedisPanelSessionFactory
    {
        public Task<IRedisPanelSession> OpenAsync(string connectionString, ConnectionProfile? tunnel, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Export must not open Redis sessions.");
    }
}
