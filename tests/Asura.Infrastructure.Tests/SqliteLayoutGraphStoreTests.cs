using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteLayoutGraphStoreTests
{
    /// <summary>
    /// The scenario the per-definition repository cannot express: a layout that
    /// grew a slot while a stored screen depends on it. Alone, the layout save
    /// is rejected for the screen's sake; batched with the reconciled screen it
    /// commits, and both land atomically.
    /// </summary>
    [Fact]
    public async Task Grown_layout_saves_with_its_reconciled_screen_in_one_batch()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database, TimeProvider.System);
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database, TimeProvider.System);
        var store = new SqliteLayoutGraphStore(temporary.Database, TimeProvider.System);

        var layout = DurableDefinitionFixtures.Layout();
        var savedLayout = await layouts.SaveAsync(layout, null, CancellationToken.None);
        Assert.True(savedLayout.IsSuccess, savedLayout.Error?.Message);
        var screen = DurableDefinitionFixtures.Screen();
        var savedScreen = await screens.SaveAsync(screen, null, CancellationToken.None);
        Assert.True(savedScreen.IsSuccess, savedScreen.Error?.Message);

        var grown = DurableDefinitionFixtures.TwoSlotLayout();
        grown = new LayoutDefinition(
            layout.Id,
            LayoutDefinition.CurrentSchemaVersion,
            layout.Name,
            grown.Grid,
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(160, 100)),
                new LayoutSlotDefinition(
                    new LayoutSlotId("added"),
                    new LayoutGridBounds(1, 0, 1, 1),
                    new LayoutMinimumSize(160, 100)),
            ]);

        // The lone save is refused: the stored screen no longer maps every slot.
        var alone = await layouts.SaveAsync(
            grown, savedLayout.Value!.Revision, CancellationToken.None);
        Assert.False(alone.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, alone.Error!.Code);

        var reconciledScreen = new ScreenDefinition(
            screen.Id,
            screen.SchemaVersion,
            screen.Name,
            screen.Description,
            screen.LayoutId,
            [
                .. screen.Panels,
                new ScreenPanelDefinition(
                    new ScreenPanelId("panel-added"),
                    new LayoutSlotId("added"),
                    ScreenPanelKind.Terminal,
                    Title: null,
                    ConnectionId: null,
                    PanelStartupBehavior.None),
            ]);

        var batched = await store.SaveLayoutWithScreensAsync(
            grown,
            savedLayout.Value.Revision,
            [new ScreenRevisionUpdate(reconciledScreen, savedScreen.Value!.Revision)],
            CancellationToken.None);

        Assert.True(batched.IsSuccess, batched.Error?.Message);
        Assert.Equal(savedLayout.Value.Revision + 1, batched.Value!.Revision);

        var reloadedLayout = await layouts.GetAsync(grown.Key, CancellationToken.None);
        Assert.True(reloadedLayout.IsSuccess);
        Assert.Equal(2, reloadedLayout.Value!.Value.Slots.Count);
        var reloadedScreen = await screens.GetAsync(screen.Key, CancellationToken.None);
        Assert.True(reloadedScreen.IsSuccess);
        Assert.Equal(2, reloadedScreen.Value!.Value.Panels.Count);
    }

    /// <summary>
    /// A stale screen revision rolls the whole batch back: the layout must not
    /// land without the screens that keep the stored graph valid.
    /// </summary>
    [Fact]
    public async Task Stale_screen_revision_rolls_back_the_layout_too()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database, TimeProvider.System);
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database, TimeProvider.System);
        var store = new SqliteLayoutGraphStore(temporary.Database, TimeProvider.System);

        var layout = DurableDefinitionFixtures.Layout();
        var savedLayout = await layouts.SaveAsync(layout, null, CancellationToken.None);
        Assert.True(savedLayout.IsSuccess, savedLayout.Error?.Message);
        var screen = DurableDefinitionFixtures.Screen();
        var savedScreen = await screens.SaveAsync(screen, null, CancellationToken.None);
        Assert.True(savedScreen.IsSuccess, savedScreen.Error?.Message);

        var renamed = new LayoutDefinition(
            layout.Id,
            LayoutDefinition.CurrentSchemaVersion,
            "Renamed",
            layout.Grid,
            layout.Slots);

        var result = await store.SaveLayoutWithScreensAsync(
            renamed,
            savedLayout.Value!.Revision,
            [new ScreenRevisionUpdate(screen, savedScreen.Value!.Revision + 7)],
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, result.Error!.Code);
        var reloaded = await layouts.GetAsync(layout.Key, CancellationToken.None);
        Assert.True(reloaded.IsSuccess);
        Assert.Equal(layout.Name, reloaded.Value!.Value.Name);
        Assert.Equal(savedLayout.Value.Revision, reloaded.Value.Revision);
    }

    /// <summary>
    /// The workspace-autosave shape: a workspace whose tab references a layout
    /// that does not exist yet commits only when both land in one graph batch.
    /// </summary>
    [Fact]
    public async Task Workspace_and_its_auto_saved_tab_layout_commit_in_one_graph_batch()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database, TimeProvider.System);
        var workspaces = new SqliteDefinitionRepository<WorkspaceDefinition>(
            temporary.Database, TimeProvider.System);
        var store = new SqliteLayoutGraphStore(temporary.Database, TimeProvider.System);

        var layout = new LayoutDefinition(
            new LayoutId($"{LayoutDefinition.AutoSaveIdPrefix}default.tab-0"),
            LayoutDefinition.CurrentSchemaVersion,
            "Terminal (auto)",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("slot-a"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(160, 100)),
            ]);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("default"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Default",
            null,
            null,
            [
                new WorkspaceEntry.Tab(
                    WorkspaceEntryId.New(),
                    "Terminal",
                    layout.Id,
                    [
                        new ScreenPanelDefinition(
                            new ScreenPanelId("panel-a"),
                            new LayoutSlotId("slot-a"),
                            ScreenPanelKind.Terminal,
                            "Terminal",
                            ConnectionId: null,
                            PanelStartupBehavior.None),
                    ]),
            ],
            autoSave: true);

        var error = await store.SaveGraphAsync(
            [
                new DefinitionGraphWrite(layout, null),
                new DefinitionGraphWrite(workspace, null),
            ],
            CancellationToken.None);

        Assert.Null(error);
        var reloadedLayout = await layouts.GetAsync(layout.Key, CancellationToken.None);
        Assert.True(reloadedLayout.IsSuccess);
        Assert.NotNull(reloadedLayout.Value);
        var reloadedWorkspace = await workspaces.GetAsync(workspace.Key, CancellationToken.None);
        Assert.True(reloadedWorkspace.IsSuccess);
        Assert.True(reloadedWorkspace.Value!.Value.AutoSave);
        Assert.Equal(
            layout.Id,
            Assert.IsType<WorkspaceEntry.Tab>(
                Assert.Single(reloadedWorkspace.Value.Value.Entries)).LayoutId);
    }

    /// <summary>
    /// A failing write anywhere in the batch rolls the whole graph back — the
    /// layouts must not land without the workspace that references them.
    /// </summary>
    [Fact]
    public async Task Failed_workspace_write_rolls_back_the_batched_layouts()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database, TimeProvider.System);
        var store = new SqliteLayoutGraphStore(temporary.Database, TimeProvider.System);

        var layout = new LayoutDefinition(
            new LayoutId($"{LayoutDefinition.AutoSaveIdPrefix}default.tab-0"),
            LayoutDefinition.CurrentSchemaVersion,
            "Terminal (auto)",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("slot-a"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(160, 100)),
            ]);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("default"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Default",
            null,
            null,
            [
                new WorkspaceEntry.Tab(
                    WorkspaceEntryId.New(),
                    "Terminal",
                    layout.Id,
                    [
                        new ScreenPanelDefinition(
                            new ScreenPanelId("panel-a"),
                            new LayoutSlotId("slot-a"),
                            ScreenPanelKind.Terminal,
                            "Terminal",
                            ConnectionId: null,
                            PanelStartupBehavior.None),
                    ]),
            ],
            autoSave: true);

        // A stale expected revision on a workspace that does not exist yet.
        var error = await store.SaveGraphAsync(
            [
                new DefinitionGraphWrite(layout, null),
                new DefinitionGraphWrite(workspace, 12),
            ],
            CancellationToken.None);

        Assert.NotNull(error);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, error.Code);
        var reloadedLayout = await layouts.GetAsync(layout.Key, CancellationToken.None);
        Assert.False(reloadedLayout.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.NotFound, reloadedLayout.Error!.Code);
    }
}
