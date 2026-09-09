using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class SavedScreenSettingsViewModelTests
{
    [Fact]
    public void New_editor_joins_catalog_options_and_excludes_auto_saved_layouts()
    {
        var selectable = Layout("layout.selectable", "Selectable");
        var autoSaved = Layout("auto.workspace.tab-1", "Auto-saved");
        var catalog = Catalog(Snapshot(selectable, autoSaved), out _);
        var aiProjectionCount = 0;
        using var settings = new SavedScreenSettingsViewModel(
            catalog,
            () =>
            {
                aiProjectionCount++;
                return [];
            });

        using var editor = settings.CreateNewEditor("  Operations  ");

        Assert.Equal("Operations", editor.Name);
        Assert.Equal(selectable.Id, editor.SelectedLayout.Id);
        Assert.DoesNotContain(editor.LayoutOptions, option => option.Id == autoSaved.Id);
        Assert.Equal(1, aiProjectionCount);
    }

    [Fact]
    public void Existing_editor_uses_the_stored_revision_and_current_catalog_options()
    {
        var layout = Layout("layout.main", "Main");
        var screen = Screen("screen.operations", "Operations", layout.Id);
        var snapshot = Snapshot(layout) with { Screens = [Store(screen, 17)] };
        using var settings = new SavedScreenSettingsViewModel(
            Catalog(snapshot, out _),
            () => []);

        using var editor = settings.CreateEditor(screen.Id);

        Assert.Equal(17, editor.ExpectedRevision);
        Assert.Equal(screen.Name, editor.Name);
        Assert.Equal(layout.Id, editor.SelectedLayout.Id);
    }

    [Fact]
    public async Task Save_forwards_the_exact_draft_and_expected_revision()
    {
        var layout = Layout("layout.main", "Main");
        var screen = Screen("screen.operations", "Operations", layout.Id);
        var catalog = Catalog(Snapshot(layout), out var recording);
        using var settings = new SavedScreenSettingsViewModel(catalog, () => []);
        var request = new SavedScreenEditorSaveRequest(screen, ExpectedRevision: 23);

        var result = await settings.SaveAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(screen, recording.LastSavedScreen);
        Assert.Equal(23, recording.LastExpectedSaveRevision);
    }

    [Fact]
    public async Task Save_preserves_catalog_revision_conflict()
    {
        var layout = Layout("layout.main", "Main");
        var screen = Screen("screen.operations", "Operations", layout.Id);
        var catalog = Catalog(Snapshot(layout), out var recording);
        var conflict = new DefinitionStoreError(
            DefinitionStoreErrorCode.RevisionConflict,
            "The saved screen changed.",
            CurrentRevision: 24);
        recording.SaveError = conflict;
        using var settings = new SavedScreenSettingsViewModel(catalog, () => []);

        var result = await settings.SaveAsync(
            new SavedScreenEditorSaveRequest(screen, ExpectedRevision: 23),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(conflict, result.Error);
        Assert.Equal(23, recording.LastExpectedSaveRevision);
    }

    [Fact]
    public async Task Stale_delete_is_rejected_before_storage_and_does_not_publish_undo()
    {
        var layout = Layout("layout.main", "Main");
        var screen = Screen("screen.operations", "Operations", layout.Id);
        var snapshot = Snapshot(layout) with { Screens = [Store(screen, 8)] };
        using var settings = new SavedScreenSettingsViewModel(
            Catalog(snapshot, out var recording),
            () => []);

        var result = await settings.DeleteAsync(
            screen.Id,
            expectedRevision: 7,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, result.Error?.Code);
        Assert.Equal(8, result.Error?.CurrentRevision);
        Assert.Equal(0, recording.DeleteCount);
        Assert.Null(settings.DeleteUndo.Pending);
    }

    [Fact]
    public async Task Successful_delete_publishes_exact_definition_and_undo_recreates_it()
    {
        var layout = Layout("layout.main", "Main");
        var screen = Screen("screen.operations", "Operations", layout.Id);
        var snapshot = Snapshot(layout) with { Screens = [Store(screen, 8)] };
        using var settings = new SavedScreenSettingsViewModel(
            Catalog(snapshot, out var recording),
            () => []);

        var deleted = await settings.DeleteAsync(
            screen.Id,
            expectedRevision: 8,
            CancellationToken.None);
        var restored = await settings.UndoDeleteAsync(CancellationToken.None);

        Assert.True(deleted.IsSuccess);
        Assert.True(restored.IsSuccess);
        Assert.Equal(screen.Key, recording.LastDeletedKey);
        Assert.Equal(8, recording.LastExpectedDeleteRevision);
        Assert.Same(screen, recording.LastSavedScreen);
        Assert.Null(recording.LastExpectedSaveRevision);
        Assert.Null(settings.DeleteUndo.Pending);
    }

    [Fact]
    public void Disposal_blocks_new_editor_and_persistence_work()
    {
        var layout = Layout("layout.main", "Main");
        var settings = new SavedScreenSettingsViewModel(
            Catalog(Snapshot(layout), out _),
            () => []);

        settings.Dispose();
        settings.Dispose();

        Assert.Throws<ObjectDisposedException>(() => settings.CreateNewEditor("Screen"));
        Assert.Throws<ObjectDisposedException>(() => settings.DismissDeleteUndo());
    }

    private static LayoutDefinition Layout(string id, string name) =>
        new(
            new LayoutId(id),
            LayoutDefinition.CurrentSchemaVersion,
            name,
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);

    private static ScreenDefinition Screen(string id, string name, LayoutId layoutId) =>
        new(
            new ScreenId(id),
            ScreenDefinition.CurrentSchemaVersion,
            name,
            "Reusable screen",
            layoutId,
            []);

    private static DefinitionCatalogSnapshot Snapshot(params LayoutDefinition[] layouts) =>
        DefinitionCatalogSnapshot.Empty with
        {
            Layouts = [.. layouts.Select((layout, index) => Store(layout, index + 1))],
        };

    private static StoredDefinition<T> Store<T>(T definition, long revision)
        where T : IDurableDefinition =>
        new(
            definition,
            revision,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static IDefinitionCatalog Catalog(
        DefinitionCatalogSnapshot snapshot,
        out RecordingCatalogProxy recording)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingCatalogProxy>();
        recording = (RecordingCatalogProxy)(object)catalog;
        recording.CurrentSnapshot = snapshot;
        return catalog;
    }

    public class RecordingCatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot CurrentSnapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public ScreenDefinition? LastSavedScreen { get; private set; }

        public long? LastExpectedSaveRevision { get; private set; }

        public DefinitionKey? LastDeletedKey { get; private set; }

        public long? LastExpectedDeleteRevision { get; private set; }

        public int DeleteCount { get; private set; }

        public DefinitionStoreError? SaveError { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            return targetMethod.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                "add_Changed" or "remove_Changed" => null,
                nameof(IDefinitionCatalog.SaveScreenAsync) => Save(
                    (ScreenDefinition)args[0]!,
                    (long?)args[1]),
                nameof(IDefinitionCatalog.DeleteAsync) => Delete(
                    (DefinitionKey)args[0]!,
                    (long)args[1]!),
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> Save(
            ScreenDefinition definition,
            long? expectedRevision)
        {
            LastSavedScreen = definition;
            LastExpectedSaveRevision = expectedRevision;
            return ValueTask.FromResult(SaveError is null
                ? DefinitionStoreResult<StoredDefinition<ScreenDefinition>>.Success(
                    Store(definition, (expectedRevision ?? 0) + 1))
                : DefinitionStoreResult<StoredDefinition<ScreenDefinition>>.Failure(
                    SaveError));
        }

        private ValueTask<DefinitionStoreResult<Unit>> Delete(
            DefinitionKey key,
            long expectedRevision)
        {
            LastDeletedKey = key;
            LastExpectedDeleteRevision = expectedRevision;
            DeleteCount++;
            return ValueTask.FromResult(DefinitionStoreResult<Unit>.Success(Unit.Value));
        }
    }
}
