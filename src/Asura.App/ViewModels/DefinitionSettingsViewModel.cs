using System.Collections.ObjectModel;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns catalog-backed layout and keybinding settings state. The shell decides
/// when these editors are visible; this type owns their drafts and revisions.
/// </summary>
public sealed class DefinitionSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private DefinitionCatalogSnapshot _snapshot;
    private LayoutDesignerViewModel? _layoutDesignerEditor;
    private KeybindingProfileItemViewModel? _selectedKeybindingProfile;
    private KeybindingEditorSessionViewModel? _keybindingEditorSession;
    private bool _disposed;

    public DefinitionSettingsViewModel(IDefinitionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _snapshot = _catalog.Snapshot;
        ApplyCatalog(_snapshot);
    }

    public ObservableCollection<LayoutCardViewModel> Layouts { get; } = [];

    public ObservableCollection<KeybindingRowViewModel> Keybindings { get; } = [];

    public ObservableCollection<KeybindingProfileItemViewModel> KeybindingProfiles { get; } = [];

    public LayoutDesignerViewModel? LayoutDesignerEditor
    {
        get => _layoutDesignerEditor;
        private set => SetProperty(ref _layoutDesignerEditor, value);
    }

    public KeybindingProfileItemViewModel? SelectedKeybindingProfile
    {
        get => _selectedKeybindingProfile;
        private set
        {
            if (SetProperty(ref _selectedKeybindingProfile, value))
            {
                OnPropertyChanged(nameof(CanCloneSelectedKeybindingProfile));
            }
        }
    }

    public KeybindingEditorSessionViewModel? KeybindingEditorSession
    {
        get => _keybindingEditorSession;
        private set
        {
            if (ReferenceEquals(_keybindingEditorSession, value))
            {
                return;
            }

            var previous = _keybindingEditorSession;
            if (SetProperty(ref _keybindingEditorSession, value))
            {
                previous?.Dispose();
                OnPropertyChanged(nameof(HasKeybindingEditor));
            }
        }
    }

    public bool HasKeybindingEditor => KeybindingEditorSession is not null;

    public bool CanCloneSelectedKeybindingProfile =>
        SelectedKeybindingProfile?.IsBuiltIn == true;

    public int KeybindingConflictCount => Keybindings.Count(item => item.HasConflict);

    public KeymapProfile ActiveApplicationKeymap =>
        ResolveActiveApplicationKeymap(_snapshot).Value;

    public long ActiveApplicationKeymapRevision =>
        ResolveActiveApplicationKeymap(_snapshot).Revision;

    public string ActiveApplicationKeymapName => ActiveApplicationKeymap.Name;

    public bool TryBeginCreateLayout(out string? error)
    {
        ThrowIfDisposed();
        if (!CanReplaceLayoutDesigner(out error))
        {
            return false;
        }

        LayoutDesignerEditor = LayoutDesignerViewModel.CreateNew();
        return true;
    }

    public bool TryBeginEditLayout(LayoutId id, out string? error)
    {
        ThrowIfDisposed();
        if (!CanReplaceLayoutDesigner(out error))
        {
            return false;
        }

        var stored = _catalog.Snapshot.Layouts.SingleOrDefault(item => item.Value.Id == id);
        if (stored is null)
        {
            error = "That layout no longer exists.";
            return false;
        }

        LayoutDesignerEditor = new LayoutDesignerViewModel(stored.Value, stored.Revision);
        error = null;
        return true;
    }

    public void DismissLayoutDesigner() => LayoutDesignerEditor = null;

    public async ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>>
        SaveLayoutDesignerAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (LayoutDesignerEditor is null)
        {
            return Fail<StoredDefinition<LayoutDefinition>>(
                "Open the layout designer before saving a layout.");
        }

        LayoutDesignerSaveRequest request;
        try
        {
            request = LayoutDesignerEditor.CreateSaveRequest();
        }
        catch (InvalidOperationException exception)
        {
            return Fail<StoredDefinition<LayoutDefinition>>(exception.Message);
        }

        return await _catalog.SaveLayoutAsync(
            request.Definition,
            request.ExpectedRevision,
            cancellationToken);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>>
        CreateLayoutAsync(
            string name,
            int rows,
            int columns,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (rows is < 1 or > 4 || columns is < 1 or > 4)
        {
            return Fail<StoredDefinition<LayoutDefinition>>(
                "Layout rows and columns must be between one and four.");
        }

        var slots = new List<LayoutSlotDefinition>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                slots.Add(new(
                    new LayoutSlotId($"slot-{row + 1}-{column + 1}"),
                    new LayoutGridBounds(column, row, 1, 1),
                    new LayoutMinimumSize(220, 140)));
            }
        }

        var geometry = new LayoutDefinition(
            LayoutId.New(),
            LayoutDefinition.CurrentSchemaVersion,
            RequireName(name, "Layout"),
            new LayoutGrid(columns, rows),
            slots);
        var definition = new LayoutDefinition(
            geometry.Id,
            geometry.SchemaVersion,
            geometry.Name,
            geometry.Grid,
            geometry.Slots,
            RuntimeDockLayoutController.SerializeDefinition(geometry));
        return await _catalog.SaveLayoutAsync(
            definition,
            expectedRevision: null,
            cancellationToken);
    }

    public ValueTask<DefinitionStoreResult<Unit>> DeleteLayoutAsync(
        LayoutId id,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _catalog.DeleteAsync(
            new DefinitionKey(LayoutDefinition.Kind, id.Value),
            expectedRevision,
            cancellationToken);
    }

    public bool TrySelectKeybindingProfile(
        KeybindingProfileItemViewModel profile,
        out string? error)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(profile);
        var stored = _catalog.Snapshot.Keymaps.SingleOrDefault(item => item.Value.Id == profile.Id);
        if (stored is null)
        {
            error = "That keybinding profile no longer exists.";
            return false;
        }

        OpenKeybindingEditor(stored.Value, stored.Revision, profile.IsBuiltIn);
        SelectedKeybindingProfile = profile;
        error = null;
        return true;
    }

    public bool TryCloneSelectedKeybindingProfile(out string? error)
    {
        ThrowIfDisposed();
        if (SelectedKeybindingProfile is not { IsBuiltIn: true } selected)
        {
            error = "Select a built-in keybinding preset to clone.";
            return false;
        }

        var source = _catalog.Snapshot.Keymaps
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == selected.Id);
        if (source is null)
        {
            error = "That built-in keybinding preset no longer exists.";
            return false;
        }

        var cloneId = new KeymapProfileId($"user.keymap.{Guid.NewGuid():N}");
        var cloneName = $"{source.Name} copy";
        var editor = KeybindingSettingsEditor.ClonePreset(
            source,
            cloneId,
            cloneName,
            BuiltInCommands.Registry);
        var profile = new KeybindingProfileItemViewModel(
            cloneId,
            Revision: null,
            cloneName,
            source.Layer,
            IsBuiltIn: false,
            IsUnsaved: true);
        KeybindingProfiles.Add(profile);
        SelectedKeybindingProfile = profile;
        KeybindingEditorSession = new KeybindingEditorSessionViewModel(
            editor,
            isReadOnly: false);
        error = null;
        return true;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>>
        SaveKeybindingEditorAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (KeybindingEditorSession is null)
        {
            return Fail<StoredDefinition<KeymapProfile>>(
                "Select a keybinding profile before saving.");
        }

        KeybindingSettingsSaveRequest request;
        try
        {
            request = KeybindingEditorSession.CreateSaveRequest();
        }
        catch (InvalidOperationException exception)
        {
            return Fail<StoredDefinition<KeymapProfile>>(exception.Message);
        }

        var result = await _catalog.SaveKeymapAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
        if (result is { IsSuccess: true, Value: { } saved })
        {
            ApplyCatalog(_catalog.Snapshot);
            var profile = KeybindingProfiles.SingleOrDefault(item => item.Id == saved.Value.Id)
                ?? new KeybindingProfileItemViewModel(
                    saved.Value.Id,
                    saved.Revision,
                    saved.Value.Name,
                    saved.Value.Layer,
                    IsBuiltInKeymap(saved.Value.Id),
                    IsUnsaved: false);
            if (KeybindingProfiles.All(item => item.Id != profile.Id))
            {
                KeybindingProfiles.Add(profile);
            }

            OpenKeybindingEditor(saved.Value, saved.Revision, profile.IsBuiltIn);
            SelectedKeybindingProfile = profile;
        }

        return result;
    }

    public ValueTask<DefinitionStoreResult<Unit>> DeleteKeymapAsync(
        KeymapProfileId id,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _catalog.DeleteAsync(
            new DefinitionKey(KeymapProfile.Kind, id.Value),
            expectedRevision,
            cancellationToken);
    }

    public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _catalog.DeleteAsync(key, expectedRevision, cancellationToken);
    }

    public void EnsureKeybindingEditor()
    {
        ThrowIfDisposed();
        if (KeybindingEditorSession is not null)
        {
            return;
        }

        var selected = KeybindingProfiles
            .FirstOrDefault(item => item.Id == ActiveApplicationKeymap.Id)
            ?? KeybindingProfiles.FirstOrDefault(item => item.Id == BuiltInKeymaps.TmuxApplicationId)
            ?? KeybindingProfiles.FirstOrDefault();
        if (selected is not null)
        {
            _ = TrySelectKeybindingProfile(selected, out _);
        }
    }

    public void ApplyCatalog(DefinitionCatalogSnapshot snapshot)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        var previousActive = ResolveActiveApplicationKeymap(_snapshot);
        _snapshot = snapshot;
        RefreshLayouts(snapshot);
        RefreshKeybindings(snapshot);
        var active = ResolveActiveApplicationKeymap(snapshot);
        if (active.Value != previousActive.Value)
        {
            OnPropertyChanged(nameof(ActiveApplicationKeymap));
            OnPropertyChanged(nameof(ActiveApplicationKeymapName));
        }

        if (active.Revision != previousActive.Revision)
        {
            OnPropertyChanged(nameof(ActiveApplicationKeymapRevision));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        KeybindingEditorSession = null;
        LayoutDesignerEditor = null;
    }

    private bool CanReplaceLayoutDesigner(out string? error)
    {
        if (LayoutDesignerEditor?.RequestCancel()
            != LayoutDesignerCancelDisposition.ConfirmDiscard)
        {
            error = null;
            return true;
        }

        error = "Save or discard the current layout changes first.";
        return false;
    }

    private void RefreshLayouts(DefinitionCatalogSnapshot snapshot)
    {
        ReplaceIfChanged(
            Layouts,
            [.. snapshot.Layouts
                .Where(item => !LayoutDefinition.IsAutoSaved(item.Value.Id))
                .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new LayoutCardViewModel(
                    item.Value.Id,
                    item.Revision,
                    item.Value.Name,
                    item.Value.Grid.Rows,
                    item.Value.Grid.Columns,
                    item.Value.Slots.Count,
                    CreateLayoutPreview(item.Value)))],
            static (left, right) => left.PresentsSameAs(right));
    }

    private void RefreshKeybindings(DefinitionCatalogSnapshot snapshot)
    {
        var transient = SelectedKeybindingProfile is { IsUnsaved: true } unsaved
            ? unsaved
            : null;
        var profiles = snapshot.Keymaps
            .OrderBy(item => item.Value.Layer)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new KeybindingProfileItemViewModel(
                item.Value.Id,
                item.Revision,
                item.Value.Name,
                item.Value.Layer,
                IsBuiltInKeymap(item.Value.Id),
                IsUnsaved: false))
            .ToList();
        if (transient is not null && profiles.All(item => item.Id != transient.Id))
        {
            profiles.Add(transient);
        }

        ReplaceIfChanged(
            KeybindingProfiles,
            profiles,
            static (left, right) => left == right);
        if (SelectedKeybindingProfile is { } selected)
        {
            SelectedKeybindingProfile = KeybindingProfiles
                .SingleOrDefault(item => item.Id == selected.Id);
        }

        var rows = new List<KeybindingRowViewModel>();
        foreach (var profile in snapshot.Keymaps.Select(item => item.Value))
        {
            var issues = KeymapConflictValidator.Validate(profile, BuiltInCommands.Registry);
            for (var bindingIndex = 0; bindingIndex < profile.Bindings.Count; bindingIndex++)
            {
                var binding = profile.Bindings[bindingIndex];
                _ = BuiltInCommands.Registry.TryGet(binding.CommandId, out var command);
                var hasConflict = issues.Any(issue =>
                    issue.BindingIndex == bindingIndex
                    || issue.OtherBindingIndex == bindingIndex);
                rows.Add(new(
                    command?.Category ?? "Unknown",
                    command?.Title ?? binding.CommandId.Value,
                    KeySequenceDisplay.Format(binding.Sequence),
                    profile.Name,
                    hasConflict ? "Conflict" : "Active",
                    hasConflict));
            }
        }

        ReplaceIfChanged(
            Keybindings,
            [.. rows
                .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Command, StringComparer.OrdinalIgnoreCase)],
            static (left, right) => left == right);
        OnPropertyChanged(nameof(KeybindingConflictCount));
    }

    private void OpenKeybindingEditor(
        KeymapProfile profile,
        long revision,
        bool isReadOnly)
    {
        var resetSource = ResolveKeybindingResetSource(profile);
        var editor = KeybindingSettingsEditor.Edit(
            profile,
            revision,
            BuiltInCommands.Registry,
            resetSource);
        KeybindingEditorSession = new KeybindingEditorSessionViewModel(editor, isReadOnly);
    }

    private KeymapProfile ResolveKeybindingResetSource(KeymapProfile profile)
    {
        var resetId = profile.BasedOn ?? profile.Id;
        return BuiltInKeymaps.All.FirstOrDefault(item => item.Id == resetId)
            ?? _catalog.Snapshot.Keymaps
                .Select(item => item.Value)
                .FirstOrDefault(item => item.Id == resetId)
            ?? profile;
    }

    private static StoredDefinition<KeymapProfile> ResolveActiveApplicationKeymap(
        DefinitionCatalogSnapshot snapshot)
    {
        var custom = snapshot.Keymaps
            .Where(item => item.Value.Layer == KeymapLayer.Application)
            .Where(item => !IsBuiltInKeymap(item.Value.Id))
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Revision)
            .ThenBy(item => item.Value.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (custom is not null)
        {
            return custom;
        }

        return snapshot.Keymaps
            .FirstOrDefault(item => item.Value.Id == BuiltInKeymaps.TmuxApplicationId)
            ?? new StoredDefinition<KeymapProfile>(
                BuiltInKeymaps.TmuxApplication,
                0,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
    }

    private static IReadOnlyList<LauncherScreenPanelPreviewViewModel> CreateLayoutPreview(
        LayoutDefinition layout) =>
        [.. layout.Slots.Select(slot => new LauncherScreenPanelPreviewViewModel(
            layout.Grid.Columns,
            layout.Grid.Rows,
            slot.Bounds.Column,
            slot.Bounds.Row,
            slot.Bounds.ColumnSpan,
            slot.Bounds.RowSpan,
            IsPrimary: false))];

    private static bool IsBuiltInKeymap(KeymapProfileId id) =>
        BuiltInKeymaps.All.Any(item => item.Id == id);

    private static DefinitionStoreResult<T> Fail<T>(string message) =>
        DefinitionStoreResult<T>.Failure(new(
            DefinitionStoreErrorCode.InvalidDefinition,
            message));

    private static string RequireName(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void ReplaceIfChanged<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> values,
        Func<T, T, bool> presentsSame)
    {
        if (target.Count == values.Count
            && target.Zip(values).All(pair => presentsSame(pair.First, pair.Second)))
        {
            return;
        }

        Replace(target, values);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
