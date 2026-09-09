using Asura.Application;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class DatabaseConnectionCatalogTests
{
    [Fact]
    public async Task Saves_lists_validates_and_deletes_database_connections()
    {
        var catalog = new DefinitionCatalog(
            new InMemoryDefinitionRepository<ConnectionProfile>(),
            new InMemoryDefinitionRepository<LayoutDefinition>(),
            new InMemoryDefinitionRepository<ScreenDefinition>(),
            new InMemoryDefinitionRepository<WorkspaceDefinition>(),
            new InMemoryDefinitionRepository<ThemePreference>(),
            new InMemoryDefinitionRepository<TerminalProfile>(),
            new InMemoryDefinitionRepository<KeymapProfile>(),
            new InMemoryDefinitionRepository<FileProviderProfile>(),
            new InMemoryDefinitionRepository<AiProviderProfile>(),
            new InMemoryDefinitionRepository<McpServerProfile>(),
            new InMemoryDefinitionRepository<QuickTerminalSettings>(),
            databaseConnections: new InMemoryDefinitionRepository<DatabaseConnectionProfile>());
        Assert.True((await catalog.InitializeAsync(CancellationToken.None)).IsSuccess);

        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "prod-core",
            "postgres",
            "Host=db.internal;Database=app");
        var saved = await catalog.SaveDatabaseConnectionAsync(
            profile,
            null,
            CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal(
            "prod-core",
            Assert.Single(catalog.Snapshot.DatabaseConnections).Value.Name);

        // Names stay unique, and a missing tunnel connection is refused.
        var duplicate = await catalog.SaveDatabaseConnectionAsync(
            new DatabaseConnectionProfile(
                DatabaseConnectionProfileId.New(),
                DatabaseConnectionProfile.CurrentSchemaVersion,
                "prod-core",
                "postgres",
                "Host=elsewhere;Database=app"),
            null,
            CancellationToken.None);
        Assert.False(duplicate.IsSuccess);

        var badTunnel = await catalog.SaveDatabaseConnectionAsync(
            new DatabaseConnectionProfile(
                DatabaseConnectionProfileId.New(),
                DatabaseConnectionProfile.CurrentSchemaVersion,
                "with-tunnel",
                "postgres",
                "Host=db;Database=app",
                passwordSecret: null,
                tunnelConnectionId: new ConnectionId("missing-bastion")),
            null,
            CancellationToken.None);
        Assert.False(badTunnel.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, badTunnel.Error!.Code);

        var deleted = await catalog.DeleteAsync(
            profile.Key,
            saved.Value!.Revision,
            CancellationToken.None);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.Empty(catalog.Snapshot.DatabaseConnections);
    }
}
