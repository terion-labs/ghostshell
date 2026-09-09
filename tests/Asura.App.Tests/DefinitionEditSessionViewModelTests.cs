using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class DefinitionEditSessionViewModelTests
{
    [Fact]
    public void Begin_and_clear_own_the_complete_metadata_draft()
    {
        var editor = new DefinitionEditSessionViewModel(
            new RecordingDefinitionCatalog(DefinitionCatalogSnapshot.Empty));
        var changed = new List<string?>();
        editor.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);

        editor.Begin(
            new DefinitionKey(WorkspaceDefinition.Kind, "operations"),
            revision: 7,
            "Operations",
            "Production workspace");

        Assert.True(editor.HasSession);
        Assert.Equal("Edit workspace", editor.EditorTitle);
        Assert.Equal("Operations", editor.EditorName);
        Assert.Equal("Production workspace", editor.EditorDescription);

        editor.Clear();

        Assert.False(editor.HasSession);
        Assert.Empty(editor.EditorName);
        Assert.Empty(editor.EditorDescription);
        Assert.Contains(
            nameof(DefinitionEditSessionViewModel.HasSession),
            changed,
            StringComparer.Ordinal);
        Assert.Contains(
            nameof(DefinitionEditSessionViewModel.EditorTitle),
            changed,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Workspace_save_uses_the_captured_revision_and_preserves_other_fields()
    {
        var workspace = Workspace();
        var catalog = new RecordingDefinitionCatalog(
            DefinitionCatalogSnapshot.Empty with
            {
                Workspaces = [Store(workspace, revision: 7)],
            });
        var editor = new DefinitionEditSessionViewModel(catalog);
        editor.Begin(workspace.Key, revision: 7, workspace.Name, workspace.Description);
        editor.EditorName = "  Renamed workspace  ";
        editor.EditorDescription = "Updated description";

        var result = await editor.SaveAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(catalog.WorkspaceSave);
        Assert.Equal(7, catalog.WorkspaceSave.ExpectedRevision);
        Assert.Equal("Renamed workspace", catalog.WorkspaceSave.Definition.Name);
        Assert.Equal("Updated description", catalog.WorkspaceSave.Definition.Description);
        Assert.Equal(workspace.Entries, catalog.WorkspaceSave.Definition.Entries);
        Assert.Equal(
            workspace.TerminalMultiplexingOverride,
            catalog.WorkspaceSave.Definition.TerminalMultiplexingOverride);
        Assert.True(catalog.WorkspaceSave.Definition.IsIsolated);
        Assert.Equal(
            workspace.IsolationMounts,
            catalog.WorkspaceSave.Definition.IsolationMounts);
    }

    [Fact]
    public async Task Screen_save_uses_the_captured_revision_and_preserves_panels()
    {
        var screen = Screen();
        var catalog = new RecordingDefinitionCatalog(
            DefinitionCatalogSnapshot.Empty with
            {
                Screens = [Store(screen, revision: 11)],
            });
        var editor = new DefinitionEditSessionViewModel(catalog);
        editor.Begin(screen.Key, revision: 11, screen.Name, screen.Description);
        editor.EditorName = "Renamed screen";

        var result = await editor.SaveAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(catalog.ScreenSave);
        Assert.Equal(11, catalog.ScreenSave.ExpectedRevision);
        Assert.Equal("Renamed screen", catalog.ScreenSave.Definition.Name);
        Assert.Equal(screen.LayoutId, catalog.ScreenSave.Definition.LayoutId);
        Assert.Equal(screen.Panels, catalog.ScreenSave.Definition.Panels);
    }

    [Fact]
    public async Task Revision_conflict_is_returned_without_ending_the_session()
    {
        var screen = Screen();
        var conflict = new DefinitionStoreError(
            DefinitionStoreErrorCode.RevisionConflict,
            "The saved screen changed.",
            CurrentRevision: 12);
        var catalog = new RecordingDefinitionCatalog(
            DefinitionCatalogSnapshot.Empty with
            {
                Screens = [Store(screen, revision: 11)],
            })
        {
            SaveError = conflict,
        };
        var editor = new DefinitionEditSessionViewModel(catalog);
        editor.Begin(screen.Key, revision: 11, screen.Name, screen.Description);

        var result = await editor.SaveAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(conflict, result.Error);
        Assert.True(editor.HasSession);
        Assert.Equal(11, catalog.ScreenSave?.ExpectedRevision);
    }

    [Fact]
    public async Task Missing_revision_and_unsupported_kind_fail_without_writing()
    {
        var workspace = Workspace();
        var catalog = new RecordingDefinitionCatalog(
            DefinitionCatalogSnapshot.Empty with
            {
                Workspaces = [Store(workspace, revision: 7)],
            });
        var editor = new DefinitionEditSessionViewModel(catalog);

        var missing = await editor.SaveAsync(CancellationToken.None);
        editor.Begin(workspace.Key, revision: null, workspace.Name, workspace.Description);
        var unsaved = await editor.SaveAsync(CancellationToken.None);
        editor.Begin(ThemePreference.Default.Key, revision: 1, "Theme", null);
        var unsupported = await editor.SaveAsync(CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, missing.Error?.Code);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, unsaved.Error?.Code);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, unsupported.Error?.Code);
        Assert.Null(catalog.WorkspaceSave);
        Assert.Null(catalog.ScreenSave);
    }

    private static WorkspaceDefinition Workspace() =>
        new(
            new WorkspaceId("operations"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            "Production workspace",
            "#336699",
            [],
            isIsolated: true,
            isolationMounts:
            [
                new(
                    Path.Combine(Path.GetTempPath(), "asura-definition-edit"),
                    "/workspace",
                    IsReadOnly: false),
            ]);

    private static ScreenDefinition Screen() =>
        new(
            new ScreenId("deploy"),
            ScreenDefinition.CurrentSchemaVersion,
            "Deploy",
            "Deployment screen",
            new LayoutId("single"),
            []);

    private static StoredDefinition<T> Store<T>(T value, long revision)
        where T : IDurableDefinition =>
        new(value, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private sealed record RecordedSave<T>(T Definition, long? ExpectedRevision)
        where T : IDurableDefinition;

    private sealed class RecordingDefinitionCatalog(DefinitionCatalogSnapshot snapshot)
        : IDefinitionCatalog
    {
        public DefinitionCatalogSnapshot Snapshot { get; } = snapshot;

        public DefinitionStoreError? SaveError { get; init; }

        public RecordedSave<WorkspaceDefinition>? WorkspaceSave { get; private set; }

        public RecordedSave<ScreenDefinition>? ScreenSave { get; private set; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
            SaveWorkspaceAsync(
                WorkspaceDefinition definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceSave = new(definition, expectedRevision);
            return Complete(definition, expectedRevision);
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>>
            SaveScreenAsync(
                ScreenDefinition definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScreenSave = new(definition, expectedRevision);
            return Complete(definition, expectedRevision);
        }

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>>
            SaveConnectionAsync(
                ConnectionProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>>
            SaveLayoutAsync(
                LayoutDefinition definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
            ThemePreference definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>>
            SaveTerminalProfileAsync(
                TerminalProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
            KeymapProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>>
            SaveFileProviderProfileAsync(
                FileProviderProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>>
            SaveAiProviderProfileAsync(
                AiProviderProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>>
            SaveMcpServerProfileAsync(
                McpServerProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>>
            SaveQuickTerminalSettingsAsync(
                QuickTerminalSettings definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
            DefinitionKey key,
            long expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private ValueTask<DefinitionStoreResult<StoredDefinition<T>>> Complete<T>(
            T definition,
            long? expectedRevision)
            where T : IDurableDefinition
        {
            var result = SaveError is null
                ? DefinitionStoreResult<StoredDefinition<T>>.Success(
                    Store(definition, (expectedRevision ?? 0) + 1))
                : DefinitionStoreResult<StoredDefinition<T>>.Failure(SaveError);
            return ValueTask.FromResult(result);
        }
    }
}
