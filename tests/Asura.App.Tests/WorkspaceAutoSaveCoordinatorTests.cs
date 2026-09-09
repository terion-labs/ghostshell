using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class WorkspaceAutoSaveCoordinatorTests
{
    private static readonly WorkspaceId WorkspaceId = new("autosave-workspace");

    [Theory]
    [InlineData("postgres", false)]
    [InlineData("redis", false)]
    [InlineData("unavailable-driver", true)]
    public async Task UnavailableDatabaseTargetsAreSealedBeforeLayoutSave(string driver, bool isolated)
    {
        using var vault = new DatabaseRecoveryStateTests.TestVault();
        var fixture = CreateUnavailableDatabase(driver + ":Host=fixture.invalid;Password=private-fixture", isolated);
        using var panel = fixture.Panel;
        using var coordinator = new WorkspaceAutoSaveCoordinator(fixture.Catalog, () => fixture.Runtime,
            () => new RuntimeHistorySource(fixture.Stored.Value.Key, fixture.Stored.Value.Name), () => false, secretVault: vault);
        coordinator.Queue();
        await coordinator.FlushAsync();
        var saved = Assert.IsType<WorkspaceDefinition>(fixture.Recorder.SavedWorkspace);
        var target = Assert.Single(Assert.IsType<WorkspaceEntry.Tab>(Assert.Single(saved.Entries)).Panels);
        var token = Assert.IsType<DatabaseRecoveryToken>(DatabaseRecoveryToken.TryParse(target.Startup.Location));
        Assert.Null(target.ConnectionId);
        Assert.Equal(["select 1"], target.Startup.Commands);
        var payload = await new DatabaseRecoveryState(vault, token).RestoreAsync(CancellationToken.None);
        Assert.Equal(driver, payload!.DriverId);
        Assert.Equal("Host=fixture.invalid;Password=private-fixture", payload.ConnectionString);
        Assert.Equal(fixture.Route, payload.Tunnel);

        fixture.Recorder.Snapshot = fixture.Recorder.Snapshot with { Workspaces = [fixture.Stored with { Value = saved }] };
        coordinator.Queue();
        await coordinator.FlushAsync();
        var repeated = Assert.Single(Assert.IsType<WorkspaceEntry.Tab>(Assert.Single(fixture.Recorder.SavedWorkspace!.Entries)).Panels);
        Assert.Equal(token.Serialize(), repeated.Startup.Location);
        Assert.Equal(["select 1"], repeated.Startup.Commands);
        Assert.Equal(1, vault.Creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedUnavailableProtectionLeavesOriginalConfigurationAndCanRetry(bool manual)
    {
        using var vault = new DatabaseRecoveryStateTests.TestVault { FailWrites = true };
        var fixture = CreateUnavailableDatabase("postgres:Host=fixture.invalid;Password=private-fixture", true, autoSave: !manual);
        using var panel = fixture.Panel;
        string? notice = null;
        using var coordinator = new WorkspaceAutoSaveCoordinator(fixture.Catalog, () => fixture.Runtime,
            () => new RuntimeHistorySource(fixture.Stored.Value.Key, fixture.Stored.Value.Name), () => false,
            secretVault: vault, reportCaptureError: text => notice = text);
        coordinator.TrackLayout(fixture.Runtime);
        Assert.True(fixture.Runtime.ActiveTab!.Rename("Renamed"));
        async Task SaveAsync()
        {
            if (manual) { _ = await coordinator.SaveLayoutAsync(fixture.Runtime, WorkspaceId, CancellationToken.None); }
            else { coordinator.Queue(); await coordinator.FlushAsync(); }
        }
        await SaveAsync();
        Assert.Null(fixture.Recorder.SavedWorkspace);
        Assert.Same(fixture.Stored.Value, Assert.Single(fixture.Recorder.Snapshot.Workspaces).Value);
        Assert.Contains("previous workspace is unchanged", notice!, StringComparison.Ordinal);
        Assert.DoesNotContain("private-fixture", notice!, StringComparison.Ordinal);
        vault.FailWrites = false;
        await SaveAsync();
        var target = Assert.Single(Assert.IsType<WorkspaceEntry.Tab>(Assert.Single(fixture.Recorder.SavedWorkspace!.Entries)).Panels);
        Assert.NotNull(DatabaseRecoveryToken.TryParse(target.Startup.Location));
        Assert.Equal(1, vault.Creates);
    }

    [Fact]
    public async Task ClosingDuringUnavailableProtectionCannotPublishTheLayout()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vault = new DatabaseRecoveryStateTests.TestVault { BeforeWrite = async () => { entered.SetResult(); await release.Task; } };
        var fixture = CreateUnavailableDatabase("postgres:Host=fixture.invalid;Password=private-fixture", true);
        using var panel = fixture.Panel;
        using var coordinator = new WorkspaceAutoSaveCoordinator(fixture.Catalog, () => fixture.Runtime,
            () => new RuntimeHistorySource(fixture.Stored.Value.Key, fixture.Stored.Value.Name), () => false, secretVault: vault);
        coordinator.Queue();
        var save = coordinator.FlushAsync();
        await entered.Task;
        coordinator.Seal();
        release.SetResult();
        await save;
        Assert.Null(fixture.Recorder.SavedWorkspace);
        Assert.Equal(0, vault.Creates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnresolvedUnavailableTargetCannotBeDroppedOrReemitted(bool missingRoute)
    {
        using var vault = new DatabaseRecoveryStateTests.TestVault();
        var fixture = CreateUnavailableDatabase(missingRoute
            ? "postgres:Host=fixture.invalid;Password=private-fixture" : "invalid-target", true);
        using var panel = fixture.Panel;
        if (missingRoute) { fixture.Recorder.Snapshot = fixture.Recorder.Snapshot with { Connections = [] }; }
        string? notice = null;
        using var coordinator = new WorkspaceAutoSaveCoordinator(fixture.Catalog, () => fixture.Runtime,
            () => new RuntimeHistorySource(fixture.Stored.Value.Key, fixture.Stored.Value.Name), () => false,
            secretVault: vault, reportCaptureError: text => notice = text);
        coordinator.Queue();
        await coordinator.FlushAsync();
        Assert.Null(fixture.Recorder.SavedWorkspace);
        Assert.Same(fixture.Stored.Value, Assert.Single(fixture.Recorder.Snapshot.Workspaces).Value);
        Assert.NotNull(notice);
        Assert.Equal(0, vault.Creates);
    }

    [Fact]
    public async Task RouteChangedDuringLaterPanelProtectionRejectsTheWholeCapture()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        using var vault = new DatabaseRecoveryStateTests.TestVault
        {
            BeforeWrite = async () => { if (++writes == 2) { entered.SetResult(); await release.Task; } },
        };
        var fixture = CreateUnavailableDatabase("postgres:Host=first.invalid;Password=first-private", true);
        using var first = fixture.Panel;
        var secondRoute = new ConnectionProfile(new ConnectionId("second-route"), 1, "Second route",
            new ConnectionEndpoint.Ssh("second.invalid", username: "fixture"), new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default, ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);
        var secondSource = new ScreenPanelDefinition(ScreenPanelId.New(), new LayoutSlotId("second-slot"),
            ScreenPanelKind.DatabaseViewer, "Second", secondRoute.Id,
            new PanelStartupBehavior("postgres:Host=second.invalid;Password=second-private", []));
        using var second = new UnavailableRuntimePanelViewModel(PanelInstanceId.New(), PanelKind.DatabaseViewer,
            "Second", "Second", "Backend unavailable")
        { SourceDefinition = secondSource };
        fixture.Runtime.ActiveTab!.AddPanel(second);
        var initialTab = Assert.IsType<WorkspaceEntry.Tab>(Assert.Single(fixture.Stored.Value.Entries));
        var initialWorkspace = new WorkspaceDefinition(WorkspaceId, 1, "Autosave", "", "#123456",
            [new WorkspaceEntry.Tab(initialTab.Id, initialTab.Name, initialTab.LayoutId, [.. initialTab.Panels, secondSource])],
            autoSave: true, isIsolated: true);
        fixture.Recorder.Snapshot = fixture.Recorder.Snapshot with
        {
            Workspaces = [fixture.Stored with { Value = initialWorkspace }],
            Connections = [.. fixture.Recorder.Snapshot.Connections,
                new(secondRoute, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
        };
        string? notice = null;
        using var coordinator = new WorkspaceAutoSaveCoordinator(fixture.Catalog, () => fixture.Runtime,
            () => new RuntimeHistorySource(fixture.Stored.Value.Key, fixture.Stored.Value.Name), () => false,
            secretVault: vault, reportCaptureError: text => notice = text);
        coordinator.Queue();
        var save = coordinator.FlushAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var changedRoute = new ConnectionProfile(fixture.Route.Id, 1, "Changed route",
            new ConnectionEndpoint.Ssh("changed.invalid", username: "fixture"), new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default, ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);
        fixture.Recorder.Snapshot = fixture.Recorder.Snapshot with
        {
            Connections = [new(changedRoute, 2, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new(secondRoute, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
        };
        release.SetResult();
        await save;
        Assert.Null(fixture.Recorder.SavedWorkspace);
        Assert.Same(initialWorkspace, Assert.Single(fixture.Recorder.Snapshot.Workspaces).Value);
        Assert.Contains("previous workspace is unchanged", notice!, StringComparison.Ordinal);
    }

    private static (IDefinitionCatalog Catalog, RecordingCatalogProxy Recorder, StoredDefinition<WorkspaceDefinition> Stored,
        RuntimeWorkspaceViewModel Runtime, UnavailableRuntimePanelViewModel Panel, ConnectionProfile Route)
        CreateUnavailableDatabase(string target, bool isolated, bool autoSave = true)
    {
        var (catalog, recorder, _) = CreateCatalog();
        var route = new ConnectionProfile(new ConnectionId("database-route"), 1, "Route",
            new ConnectionEndpoint.Ssh("route.invalid", username: "fixture"), new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default, ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);
        var source = new ScreenPanelDefinition(ScreenPanelId.New(), new LayoutSlotId("database-slot"),
            ScreenPanelKind.DatabaseViewer, "Database", route.Id, new PanelStartupBehavior(target, ["select 1"]));
        var workspace = new WorkspaceDefinition(WorkspaceId, 1, "Autosave", "", "#123456",
            [new WorkspaceEntry.Tab(WorkspaceEntryId.New(), "Database", new LayoutId("layout"), [source])],
            autoSave: autoSave, isIsolated: isolated);
        var stored = new StoredDefinition<WorkspaceDefinition>(workspace, 7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        recorder.Snapshot = recorder.Snapshot with
        {
            Workspaces = [stored],
            Connections = [new(route, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
        };
        var runtime = new RuntimeWorkspaceViewModel(WorkspaceInstanceId.New(), "Autosave", "#123456", []);
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Database", "Database");
        var panel = new UnavailableRuntimePanelViewModel(PanelInstanceId.New(), PanelKind.DatabaseViewer, "Database", "Database",
            isolated ? "The isolated backend is unavailable." : "The database drivers are unavailable.")
        { SourceDefinition = source };
        tab.AddPanel(panel);
        runtime.Tabs.Add(tab);
        runtime.ActiveTab = tab;
        return (catalog, recorder, stored, runtime, panel, route);
    }

    internal static ScreenPanelDefinition CaptureDatabasePanel(RuntimePanelViewModel panel, ScreenPanelDefinition source)
    {
        var tab = new WorkspaceEntry.Tab(WorkspaceEntryId.New(), "Database", new LayoutId("layout"), [source]);
        var capture = typeof(WorkspaceAutoSaveCoordinator).GetMethod("CaptureAutoSavePanel",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ScreenPanelDefinition)capture.Invoke(null,
            [panel, source.SlotId, tab, new[] { tab }, new HashSet<ScreenPanelId>(), null])!;
    }

    [Fact]
    public async Task PendingDatabaseRestoreAutosavesOnlyItsOpaqueTarget()
    {
        var (catalog, recorder, stored) = CreateCatalog();
        var runtime = new RuntimeWorkspaceViewModel(WorkspaceInstanceId.New(), "Autosave", "#123456", []);
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Database", "Database");
        var token = DatabaseRecoveryToken.Create().Serialize();
        using var pending = new PendingDatabaseRecoveryPanelViewModel(PanelInstanceId.New(), "Database", token,
            (_, _) => Task.FromResult(false));
        tab.AddPanel(pending);
        runtime.Tabs.Add(tab);
        runtime.ActiveTab = tab;
        using var coordinator = new WorkspaceAutoSaveCoordinator(catalog, () => runtime,
            () => new RuntimeHistorySource(stored.Value.Key, stored.Value.Name), () => false);
        coordinator.Queue();
        await coordinator.FlushAsync();

        var saved = Assert.IsType<WorkspaceDefinition>(recorder.SavedWorkspace);
        var savedPanel = Assert.Single(Assert.IsType<WorkspaceEntry.Tab>(Assert.Single(saved.Entries)).Panels);
        Assert.Equal(token, savedPanel.Startup.Location);
        Assert.Null(savedPanel.ConnectionId);
        Assert.Contains(token, RuntimeWorkspaceRecoveryCodec.Serialize(runtime), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flush_writes_the_captured_workspace_without_waiting_for_the_debounce()
    {
        var (catalog, recorder, stored) = CreateCatalog();
        var runtime = CreateLauncherWorkspace();
        using var coordinator = new WorkspaceAutoSaveCoordinator(
            catalog,
            () => runtime,
            () => new RuntimeHistorySource(stored.Value.Key, stored.Value.Name),
            () => false);

        coordinator.Queue();
        await coordinator.FlushAsync();

        var saved = Assert.IsType<WorkspaceDefinition>(recorder.SavedWorkspace);
        Assert.Equal(WorkspaceId, saved.Id);
        Assert.True(saved.AutoSave);
        Assert.True(saved.IsIsolated);
        Assert.Equal(stored.Value.IsolationMounts, saved.IsolationMounts);
        Assert.Empty(saved.Entries);
        Assert.Equal(stored.Revision, recorder.ExpectedRevision);
    }

    [Fact]
    public async Task Seal_rejects_new_queue_and_flush_requests()
    {
        var (catalog, recorder, stored) = CreateCatalog();
        using var coordinator = new WorkspaceAutoSaveCoordinator(
            catalog,
            CreateLauncherWorkspace,
            () => new RuntimeHistorySource(stored.Value.Key, stored.Value.Name),
            () => false);

        coordinator.Seal();
        coordinator.Queue();
        await coordinator.FlushAsync();

        Assert.Null(recorder.SavedWorkspace);
    }

    private static RuntimeWorkspaceViewModel CreateLauncherWorkspace()
    {
        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Autosave",
            "#123456",
            []);
        var launcher = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "New tab",
            "Launcher");
        launcher.AddPlaceholder(PanelSide.Right);
        runtime.Tabs.Add(launcher);
        runtime.ActiveTab = launcher;
        return runtime;
    }

    private static (
        IDefinitionCatalog Catalog,
        RecordingCatalogProxy Recorder,
        StoredDefinition<WorkspaceDefinition> Stored) CreateCatalog()
    {
        var workspace = new WorkspaceDefinition(
            WorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Autosave",
            "",
            "#123456",
            [new WorkspaceEntry.ConnectionReference(
                WorkspaceEntryId.New(),
                new ConnectionId("local"))],
            autoSave: true,
            isIsolated: true,
            isolationMounts:
            [
                new(
                    Path.Combine(Path.GetTempPath(), "asura-autosave"),
                    "/workspace",
                    IsReadOnly: false),
            ]);
        var stored = new StoredDefinition<WorkspaceDefinition>(
            workspace,
            7,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingCatalogProxy>();
        var recorder = (RecordingCatalogProxy)(object)catalog;
        recorder.Snapshot = DefinitionCatalogSnapshot.Empty with { Workspaces = [stored] };
        return (catalog, recorder, stored);
    }

    public class RecordingCatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot Snapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public WorkspaceDefinition? SavedWorkspace { get; private set; }

        public long? ExpectedRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => Snapshot,
                "add_Changed" or "remove_Changed" => null,
                "SaveWorkspaceWithLayoutsAsync" => Save(args!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private object Save(object?[] args)
        {
            SavedWorkspace = (WorkspaceDefinition)args[0]!;
            ExpectedRevision = (long?)args[1];
            return ValueTask.FromResult<DefinitionStoreError?>(null);
        }
    }
}
