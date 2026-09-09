using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class WorkspaceSettingsViewModelTests
{
    [Fact]
    public void Begin_edit_projects_the_catalog_and_publishes_revision_identity()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);

        var opened = viewModel.TryBeginEdit(
            WorkspaceId,
            out var identity,
            out var error);

        Assert.True(opened);
        Assert.Null(error);
        Assert.Equal(WorkspaceRevision, identity?.Revision);
        Assert.Equal("Operations", identity?.Name);
        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.Editor);
        Assert.Equal(WorkspaceRevision, editor.ExpectedRevision);
        Assert.Contains(editor.ConnectionOptions, option => option.Id == ConnectionId);
        Assert.Contains(editor.LayoutOptions, option => option.Id == LayoutId);
        Assert.Contains(editor.ScreenOptions, option => option.Id == ScreenId);
        Assert.Equal([WorkspaceMount], editor.CreateSaveRequest().Definition.IsolationMounts);
        Assert.True(viewModel.HasEditor);
    }

    [Fact]
    public void Open_workspace_projects_an_editable_isolation_configuration()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(
            fixture.Catalog,
            isWorkspaceOpen: key => key == WorkspaceMountOwnerKey);

        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));

        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.Editor);
        Assert.True(editor.CanToggleIsolation);
        Assert.True(editor.CanAddIsolationMount);
        Assert.Equal([WorkspaceMount], editor.CreateSaveRequest().Definition.IsolationMounts);
    }

    [Fact]
    public void Missing_runtime_name_is_projected_into_the_workspace_editor()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(
            fixture.Catalog,
            isIsolationAvailable: false,
            isolationRuntimeDisplayName: "Apple container");

        Assert.True(viewModel.TryBeginCreate(out _, out _));

        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.Editor);
        Assert.True(editor.CanInstallIsolationRuntime);
        Assert.Equal(
            "Install Apple container to enable isolation",
            editor.IsolationRuntimeRequirementLabel);
        Assert.Equal("Install Apple container\u2026", editor.InstallIsolationRuntimeLabel);
    }

    [Fact]
    public void Dirty_draft_blocks_replacement_and_preserves_the_editor()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.Editor);
        editor.Name = "Unsaved draft";

        var opened = viewModel.TryBeginCreate(out var identity, out var error);

        Assert.False(opened);
        Assert.Null(identity);
        Assert.Contains("discard", error, StringComparison.OrdinalIgnoreCase);
        Assert.Same(editor, viewModel.Editor);
        Assert.Equal("Unsaved draft", editor.Name);
    }

    [Fact]
    public async Task Successful_save_forwards_revision_and_dismisses_the_editor()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        viewModel.Editor!.Name = "Production";

        var result = await viewModel.SaveAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceRevision, fixture.Proxy.LastExpectedRevision);
        Assert.Equal("Production", fixture.Proxy.LastSavedWorkspace?.Name);
        Assert.Null(viewModel.Editor);
        Assert.False(viewModel.HasEditor);
    }

    [Fact]
    public async Task Revision_conflict_preserves_the_live_draft()
    {
        var fixture = CreateCatalog(Snapshot());
        fixture.Proxy.RejectSave = true;
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.Editor);
        editor.Name = "Conflicting draft";

        var result = await viewModel.SaveAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, result.Error?.Code);
        Assert.Equal(WorkspaceRevision, fixture.Proxy.LastExpectedRevision);
        Assert.Same(editor, viewModel.Editor);
        Assert.Equal("Conflicting draft", editor.Name);
    }

    [Fact]
    public async Task Stale_request_is_rejected_before_catalog_persistence()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        var current = viewModel.Editor!.CreateSaveRequest();
        var stale = current with { ExpectedRevision = current.ExpectedRevision + 1 };

        var result = await viewModel.SaveAsync(stale, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, result.Error?.Code);
        Assert.Null(fixture.Proxy.LastSavedWorkspace);
        Assert.NotNull(viewModel.Editor);
    }

    [Fact]
    public async Task Open_workspace_rejects_a_request_that_changes_host_mounts()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(
            fixture.Catalog,
            isWorkspaceOpen: key => key == WorkspaceMountOwnerKey);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        var current = viewModel.Editor!.CreateSaveRequest();
        var changedMount = new WorkspaceIsolationMountDefinition(
            Path.Combine(Path.GetTempPath(), "asura-settings", "changed"),
            "/changed",
            IsReadOnly: true);
        var changed = current with
        {
            Definition = CopyWithIsolationMounts(current.Definition, [changedMount]),
        };

        var result = await viewModel.SaveAsync(changed, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, result.Error?.Code);
        Assert.Contains("Close this workspace", result.Error?.Message, StringComparison.Ordinal);
        Assert.Null(fixture.Proxy.LastSavedWorkspace);
        Assert.NotNull(viewModel.Editor);
    }

    [Fact]
    public async Task Open_workspace_can_save_changes_outside_the_isolation_boundary()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(
            fixture.Catalog,
            isWorkspaceOpen: key => key == WorkspaceMountOwnerKey);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        viewModel.Editor!.Name = "Renamed while open";

        var result = await viewModel.SaveAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Renamed while open", fixture.Proxy.LastSavedWorkspace?.Name);
        Assert.Equal([WorkspaceMount], fixture.Proxy.LastSavedWorkspace?.IsolationMounts);
    }

    [Fact]
    public async Task Cold_configuration_save_blocks_a_runtime_reservation_until_persistence_finishes()
    {
        var fixture = CreateCatalog(Snapshot());
        fixture.Proxy.BlockSave = true;
        var occupancy = new WorkspaceDefinitionOccupancy();
        using var viewModel = new WorkspaceSettingsViewModel(
            fixture.Catalog,
            workspaceDefinitionOccupancy: occupancy);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        var current = viewModel.Editor!.CreateSaveRequest();
        var changedMount = WorkspaceMount with { GuestPath = "/changed" };
        var changed = current with
        {
            Definition = CopyWithIsolationMounts(
                current.Definition,
                [changedMount]),
        };

        var saving = viewModel.SaveAsync(changed, CancellationToken.None).AsTask();
        await fixture.Proxy.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var windowId = new WindowInstanceId("settings-race-window");
        var runtimeId = new WorkspaceInstanceId("settings-race-runtime");
        Assert.False(occupancy.TryRegisterRuntime(
            windowId,
            runtimeId,
            WorkspaceMountOwnerKey));

        fixture.Proxy.AllowSave.TrySetResult(true);
        var result = await saving;
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(occupancy.TryRegisterRuntime(
            windowId,
            runtimeId,
            WorkspaceMountOwnerKey));
        occupancy.Unregister(windowId, runtimeId);
    }

    [Fact]
    public async Task Create_normalizes_the_name_and_uses_a_null_revision()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);

        var result = await viewModel.CreateAsync("  New workspace  ", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("New workspace", fixture.Proxy.LastSavedWorkspace?.Name);
        Assert.Null(fixture.Proxy.LastSavedWorkspace?.Accent);
        Assert.False(fixture.Proxy.LastSavedWorkspace?.HasExplicitAccent);
        Assert.Null(fixture.Proxy.LastExpectedRevision);
    }

    [Fact]
    public async Task Isolation_list_toggle_preserves_mounts_and_definition_fields()
    {
        var fixture = CreateCatalog(Snapshot());
        var stored = fixture.Proxy.CurrentSnapshot.Workspaces.Single();
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);

        var result = await viewModel.SetIsolationAsync(
            WorkspaceId,
            isIsolated: false,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(fixture.Proxy.LastSavedWorkspace?.IsIsolated);
        Assert.Equal([WorkspaceMount], fixture.Proxy.LastSavedWorkspace?.IsolationMounts);
        Assert.Equal(stored.Value.Name, fixture.Proxy.LastSavedWorkspace?.Name);
        Assert.Equal(stored.Revision, fixture.Proxy.LastExpectedRevision);
    }

    [Fact]
    public async Task Isolation_list_toggle_rejects_an_open_workspace()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(
            fixture.Catalog,
            isWorkspaceOpen: key => key == WorkspaceMountOwnerKey);

        var result = await viewModel.SetIsolationAsync(
            WorkspaceId,
            isIsolated: false,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Close this workspace", result.Error?.Message, StringComparison.Ordinal);
        Assert.Null(fixture.Proxy.LastSavedWorkspace);
    }

    [Fact]
    public async Task Isolation_list_toggle_requires_an_available_runtime_when_enabling()
    {
        var snapshot = Snapshot();
        var stored = snapshot.Workspaces.Single();
        var unisolated = CopyWithIsolation(stored.Value, isIsolated: false);
        var fixture = CreateCatalog(snapshot with
        {
            Workspaces = [Store(unisolated, stored.Revision)],
        });
        using var viewModel = new WorkspaceSettingsViewModel(
            fixture.Catalog,
            isIsolationAvailable: false);

        var result = await viewModel.SetIsolationAsync(
            WorkspaceId,
            isIsolated: true,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Install", result.Error?.Message, StringComparison.Ordinal);
        Assert.Null(fixture.Proxy.LastSavedWorkspace);
    }

    [Fact]
    public void New_editor_follows_the_system_accent_until_the_user_overrides_it()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);

        Assert.True(viewModel.TryBeginCreate(out _, out _));

        var definition = viewModel.Editor!.CreateSaveRequest().Definition;
        Assert.Null(definition.Accent);
        Assert.False(definition.HasExplicitAccent);
    }

    [Fact]
    public async Task Agent_panel_pin_preserves_the_definition_and_revision()
    {
        var fixture = CreateCatalog(Snapshot());
        var stored = fixture.Proxy.CurrentSnapshot.Workspaces.Single();
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);

        var result = await viewModel.SetAgentPanelPinnedAsync(
            stored.Value.Key,
            isPinned: true,
            CancellationToken.None);

        Assert.True(result is { IsSuccess: true });
        Assert.True(fixture.Proxy.LastSavedWorkspace?.AgentPanelPinned);
        Assert.True(fixture.Proxy.LastSavedWorkspace?.IsIsolated);
        Assert.Equal([WorkspaceMount], fixture.Proxy.LastSavedWorkspace?.IsolationMounts);
        Assert.Equal(stored.Value.Name, fixture.Proxy.LastSavedWorkspace?.Name);
        Assert.Equal(stored.Revision, fixture.Proxy.LastExpectedRevision);
    }

    [Fact]
    public void Disposing_the_owner_disposes_and_releases_the_editor()
    {
        var fixture = CreateCatalog(Snapshot());
        var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.Editor);

        viewModel.Dispose();
        viewModel.Dispose();

        Assert.Null(viewModel.Editor);
        Assert.True(IsDisposed(editor));
    }

    private const long WorkspaceRevision = 17;
    private static readonly WorkspaceId WorkspaceId = new("workspace.settings-owner");
    private static readonly ConnectionId ConnectionId = new("connection.settings-owner");
    private static readonly LayoutId LayoutId = new("layout.settings-owner");
    private static readonly ScreenId ScreenId = new("screen.settings-owner");
    private static readonly DefinitionKey WorkspaceMountOwnerKey = new(
        WorkspaceDefinition.Kind,
        WorkspaceId.Value);
    private static readonly WorkspaceIsolationMountDefinition WorkspaceMount = new(
        Path.Combine(Path.GetTempPath(), "asura-settings", "workspace"),
        "/workspace",
        IsReadOnly: false);

    private static bool IsDisposed(WorkspaceEditorViewModel editor) =>
        (bool)typeof(WorkspaceEditorViewModel)
            .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;

    private static DefinitionCatalogSnapshot Snapshot()
    {
        var connection = new ConnectionProfile(
            ConnectionId,
            ConnectionProfile.CurrentSchemaVersion,
            "Local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var layout = new LayoutDefinition(
            LayoutId,
            LayoutDefinition.CurrentSchemaVersion,
            "Single",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var screen = new ScreenDefinition(
            ScreenId,
            ScreenDefinition.CurrentSchemaVersion,
            "Shell",
            null,
            LayoutId,
            []);
        var workspace = new WorkspaceDefinition(
            WorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            "Production workspace",
            ThemePreference.BronzeFallback.ToString(),
            [],
            isIsolated: true,
            isolationMounts: [WorkspaceMount]);
        return DefinitionCatalogSnapshot.Empty with
        {
            Connections = [Store(connection, 3)],
            Layouts = [Store(layout, 5)],
            Screens = [Store(screen, 7)],
            Workspaces = [Store(workspace, WorkspaceRevision)],
        };
    }

    private static WorkspaceDefinition CopyWithIsolationMounts(
        WorkspaceDefinition current,
        IReadOnlyList<WorkspaceIsolationMountDefinition> isolationMounts) =>
        new(
            current.Id,
            current.SchemaVersion,
            current.Name,
            current.Description,
            current.Accent,
            current.Entries,
            current.AgentPolicyOverride,
            current.Icon,
            current.AutoSave,
            current.Color,
            current.AgentPanelPinned,
            current.TerminalMultiplexingOverride,
            current.BrowserProfileOverride,
            current.HasExplicitAccent,
            current.IsIsolated,
            isolationMounts);

    private static WorkspaceDefinition CopyWithIsolation(
        WorkspaceDefinition current,
        bool isIsolated) =>
        new(
            current.Id,
            current.SchemaVersion,
            current.Name,
            current.Description,
            current.Accent,
            current.Entries,
            current.AgentPolicyOverride,
            current.Icon,
            current.AutoSave,
            current.Color,
            current.AgentPanelPinned,
            current.TerminalMultiplexingOverride,
            current.BrowserProfileOverride,
            current.HasExplicitAccent,
            isIsolated,
            current.IsolationMounts);

    private static StoredDefinition<T> Store<T>(T value, long revision)
        where T : IDurableDefinition =>
        new(value, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static CatalogFixture CreateCatalog(DefinitionCatalogSnapshot snapshot)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingCatalogProxy>();
        var proxy = (RecordingCatalogProxy)(object)catalog;
        proxy.CurrentSnapshot = snapshot;
        return new(catalog, proxy);
    }

    private sealed record CatalogFixture(
        IDefinitionCatalog Catalog,
        RecordingCatalogProxy Proxy);

    public class RecordingCatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot CurrentSnapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public bool RejectSave { get; set; }

        public bool BlockSave { get; set; }

        public TaskCompletionSource<bool> SaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> AllowSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkspaceDefinition? LastSavedWorkspace { get; private set; }

        public long? LastExpectedRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            return targetMethod.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                nameof(IDefinitionCatalog.SaveWorkspaceAsync) => SaveWorkspace(
                    (WorkspaceDefinition)args[0]!,
                    (long?)args[1],
                    (CancellationToken)args[2]!),
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }

        private async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
            SaveWorkspace(
                WorkspaceDefinition definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            LastSavedWorkspace = definition;
            LastExpectedRevision = expectedRevision;
            SaveEntered.TrySetResult(true);
            if (BlockSave)
            {
                await AllowSave.Task.WaitAsync(cancellationToken);
            }

            if (RejectSave)
            {
                return DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Failure(new(
                    DefinitionStoreErrorCode.RevisionConflict,
                    "The workspace changed before it could be saved.",
                    (expectedRevision ?? 0) + 1));
            }

            return DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Success(
                Store(definition, (expectedRevision ?? 0) + 1));
        }
    }
}
