using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns saved-screen authoring, optimistic persistence, deletion, and the
/// one-level delete undo. Runtime instances are deliberately outside this boundary.
/// </summary>
public sealed class SavedScreenSettingsViewModel : IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private readonly Func<IReadOnlyList<AiProviderProfileDescriptor>> _aiProviders;
    private bool _disposed;

    public SavedScreenSettingsViewModel(
        IDefinitionCatalog catalog,
        Func<IReadOnlyList<AiProviderProfileDescriptor>> aiProviders)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _aiProviders = aiProviders ?? throw new ArgumentNullException(nameof(aiProviders));
        DeleteUndo = new SavedScreenDeleteUndoViewModel(_catalog);
    }

    public SavedScreenDeleteUndoViewModel DeleteUndo { get; }

    public SavedScreenEditorViewModel CreateEditor(ScreenId screenId)
    {
        ThrowIfDisposed();
        var snapshot = _catalog.Snapshot;
        var stored = snapshot.Screens
            .SingleOrDefault(item => item.Value.Id == screenId)
            ?? throw new InvalidOperationException("That saved screen no longer exists.");
        return new SavedScreenEditorViewModel(
            stored.Value,
            stored.Revision,
            [.. snapshot.Connections.Select(item => item.Value)],
            [.. snapshot.FileProviderProfiles.Select(item => item.Value)],
            SelectableLayouts(snapshot),
            _aiProviders());
    }

    public SavedScreenEditorViewModel CreateNewEditor(string name)
    {
        ThrowIfDisposed();
        var snapshot = _catalog.Snapshot;
        return SavedScreenEditorViewModel.CreateNew(
            RequireName(name, "Saved screen"),
            SelectableLayouts(snapshot),
            [.. snapshot.Connections.Select(item => item.Value)],
            [.. snapshot.FileProviderProfiles.Select(item => item.Value)],
            _aiProviders());
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveAsync(
        SavedScreenEditorSaveRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        return _catalog.SaveScreenAsync(
            request.Definition,
            request.ExpectedRevision,
            cancellationToken);
    }

    public async ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        ScreenId screenId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var current = _catalog.Snapshot.Screens
            .SingleOrDefault(item => item.Value.Id == screenId);
        if (current is null)
        {
            return Failure(
                DefinitionStoreErrorCode.NotFound,
                "That saved screen no longer exists.");
        }

        if (current.Revision != expectedRevision)
        {
            return Failure(
                DefinitionStoreErrorCode.RevisionConflict,
                "That saved screen changed before it could be deleted.",
                current.Revision);
        }

        var result = await _catalog.DeleteAsync(
            current.Value.Key,
            expectedRevision,
            cancellationToken);
        if (result.IsSuccess)
        {
            DeleteUndo.Publish(current);
        }

        return result;
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> UndoDeleteAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return DeleteUndo.UndoAsync(cancellationToken);
    }

    public void DismissDeleteUndo()
    {
        ThrowIfDisposed();
        DeleteUndo.Dismiss();
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private static LayoutDefinition[] SelectableLayouts(
        DefinitionCatalogSnapshot snapshot) =>
        [.. snapshot.Layouts
            .Select(item => item.Value)
            .Where(layout => !LayoutDefinition.IsAutoSaved(layout.Id))];

    private static DefinitionStoreResult<Unit> Failure(
        DefinitionStoreErrorCode code,
        string message,
        long? currentRevision = null) =>
        DefinitionStoreResult<Unit>.Failure(new(
            code,
            message,
            currentRevision));

    private static string RequireName(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
