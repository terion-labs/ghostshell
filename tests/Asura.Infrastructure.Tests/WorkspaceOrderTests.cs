using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class WorkspaceOrderTests
{
    [Fact]
    public async Task Reordered_workspaces_survive_database_reopen_and_keep_their_settings()
    {
        await using var database = TemporaryDatabase.Create();
        var catalog = await CreateCatalogAsync(database.Database);
        var original = catalog.Snapshot.Workspaces.ToDictionary(item => item.Value.Id, item => item.Value);
        var ids = original.Keys.Reverse().ToArray();

        Assert.Null(await catalog.ReorderWorkspacesAsync(ids, CancellationToken.None));
        var revisions = catalog.Snapshot.Workspaces.ToDictionary(item => item.Value.Id, item => item.Revision);
        Assert.Null(await catalog.ReorderWorkspacesAsync(ids, CancellationToken.None));
        Assert.All(catalog.Snapshot.Workspaces, item => Assert.Equal(revisions[item.Value.Id], item.Revision));

        await database.ReopenAsync();
        var restarted = CreateCatalog(database.Database);
        Assert.True((await restarted.InitializeAsync(CancellationToken.None)).IsSuccess);
        var ordered = restarted.Snapshot.Workspaces.OrderBy(item => item.Value.SortOrder).ToArray();
        Assert.Equal(ids, ordered.Select(item => item.Value.Id));
        for (var index = 0; index < ordered.Length; index++)
        {
            var actual = ordered[index].Value;
            Assert.Equal(index, actual.SortOrder);
            Assert.Equal(original[actual.Id].Name, actual.Name);
            Assert.Equal(original[actual.Id].Description, actual.Description);
            Assert.Equal(original[actual.Id].AutoSave, actual.AutoSave);
            Assert.Equal(original[actual.Id].BrowserProfileOverride, actual.BrowserProfileOverride);
        }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("unknown")]
    public async Task Invalid_membership_does_not_write_positions(string kind)
    {
        await using var database = TemporaryDatabase.Create();
        var catalog = await CreateCatalogAsync(database.Database);
        var before = catalog.Snapshot;
        var ids = before.Workspaces.Select(item => item.Value.Id).ToList();
        switch (kind)
        {
            case "duplicate": ids[0] = ids[1]; break;
            case "missing": ids.RemoveAt(0); break;
            case "unknown": ids[0] = new WorkspaceId("unknown"); break;
        }

        var error = await catalog.ReorderWorkspacesAsync(ids, CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, error?.Code);
        Assert.Same(before, catalog.Snapshot);
        var repository = new SqliteDefinitionRepository<WorkspaceDefinition>(database.Database, TimeProvider.System);
        foreach (var workspace in before.Workspaces)
        {
            var stored = await repository.GetAsync(workspace.Value.Key, CancellationToken.None);
            Assert.Equal(workspace.Revision, stored.Value!.Revision);
            Assert.Equal(int.MaxValue, stored.Value.Value.SortOrder);
        }
    }

    [Fact]
    public async Task Concurrent_edit_rolls_back_all_positions_without_overwriting_the_edit()
    {
        await using var database = TemporaryDatabase.Create();
        var catalog = await CreateCatalogAsync(database.Database);
        var before = catalog.Snapshot;
        var repository = new SqliteDefinitionRepository<WorkspaceDefinition>(database.Database, TimeProvider.System);
        var edited = before.Workspaces[^1];
        Assert.True((await repository.SaveAsync(edited.Value with { SortOrder = 42 }, edited.Revision, CancellationToken.None)).IsSuccess);

        var error = await catalog.ReorderWorkspacesAsync(
            [.. before.Workspaces.Select(item => item.Value.Id)], CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, error?.Code);
        Assert.Same(before, catalog.Snapshot);
        foreach (var workspace in before.Workspaces)
        {
            var stored = await repository.GetAsync(workspace.Value.Key, CancellationToken.None);
            Assert.Equal(workspace.Value.Id == edited.Value.Id ? 42 : int.MaxValue, stored.Value!.Value.SortOrder);
        }
    }

    private static async Task<DefinitionCatalog> CreateCatalogAsync(AsuraDatabase database)
    {
        var catalog = CreateCatalog(database);
        Assert.True((await catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        foreach (var name in new[] { "Alpha", "Beta" })
        {
            var workspace = new WorkspaceDefinition(new WorkspaceId(name), WorkspaceDefinition.CurrentSchemaVersion,
                name, "Preserved description", null, [], autoSave: true);
            Assert.True((await catalog.SaveWorkspaceAsync(workspace, null, CancellationToken.None)).IsSuccess);
        }
        return catalog;
    }

    private static DefinitionCatalog CreateCatalog(AsuraDatabase database) => new(
        new SqliteDefinitionRepository<ConnectionProfile>(database, TimeProvider.System),
        new SqliteDefinitionRepository<LayoutDefinition>(database, TimeProvider.System),
        new SqliteDefinitionRepository<ScreenDefinition>(database, TimeProvider.System),
        new SqliteDefinitionRepository<WorkspaceDefinition>(database, TimeProvider.System),
        new SqliteDefinitionRepository<ThemePreference>(database, TimeProvider.System),
        new SqliteDefinitionRepository<TerminalProfile>(database, TimeProvider.System),
        new SqliteDefinitionRepository<KeymapProfile>(database, TimeProvider.System),
        new SqliteDefinitionRepository<FileProviderProfile>(database, TimeProvider.System),
        new SqliteDefinitionRepository<AiProviderProfile>(database, TimeProvider.System),
        new SqliteDefinitionRepository<McpServerProfile>(database, TimeProvider.System),
        new SqliteDefinitionRepository<QuickTerminalSettings>(database, TimeProvider.System),
        layoutGraph: new SqliteLayoutGraphStore(database, TimeProvider.System));
}
