using System.Collections.ObjectModel;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record KeybindingProfileItemViewModel(
    KeymapProfileId Id,
    long? Revision,
    string Name,
    KeymapLayer Layer,
    bool IsBuiltIn,
    bool IsUnsaved)
{
    public string LayerLabel => Layer.ToString();

    public string StateLabel => IsUnsaved ? "Unsaved" : IsBuiltIn ? "Preset" : "Custom";
}

public sealed record KeybindingEditorRowItemViewModel(
    KeybindingEditorRow Row,
    bool CanEdit)
{
    public KeybindingEditorRowId Id => Row.Id;

    public string Title => Row.Title;

    public string Category => Row.Category;

    /// <summary>
    /// The stored form shouts key names so strokes compare exactly; the table
    /// shows the keyboard's own symbols and casing instead.
    /// </summary>
    public string Shortcut => Row.Sequence is null
        ? Row.Shortcut
        : KeySequenceDisplay.Format(Row.Sequence);

    public string Contexts => Row.Contexts.ToString();

    public bool IsBound => Row.IsBound;

    public bool CanReset => Row.CanReset;

    public bool HasBlockingConflict => Row.HasBlockingConflict;

    public bool IsUnknownCommand => Row.IsUnknownCommand;

    public bool CanUnbind => CanEdit && IsBound;

    public bool CanResetShortcut => CanEdit && CanReset;

    public bool HasIssue => Row.Issues.Count > 0;

    public string Status => Row.HasBlockingConflict
        ? "Conflict"
        : Row.IsUnknownCommand
            ? "Unknown"
            : Row.IsBound
                ? "Active"
                : "Unbound";

    public string IssueSummary => string.Join(' ', Row.Issues.Select(issue => issue.Message));
}

/// <summary>
/// Adapts the framework-independent keybinding draft to observable settings state. Persistence and
/// native shortcut capture stay outside this type.
/// </summary>
public sealed class KeybindingEditorSessionViewModel : ObservableObject, IDisposable
{
    private readonly KeybindingSettingsEditor _editor;
    private string _query = string.Empty;
    private bool _disposed;

    public KeybindingEditorSessionViewModel(
        KeybindingSettingsEditor editor,
        bool isReadOnly)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        IsReadOnly = isReadOnly;
        _editor.Changed += OnEditorChanged;
        Refresh();
    }

    public ObservableCollection<KeybindingEditorRowItemViewModel> Rows { get; } = [];

    public IReadOnlyList<FailedSequenceBehavior> FailedSequenceBehaviors { get; } =
        Enum.GetValues<FailedSequenceBehavior>();

    public KeybindingSettingsEditor Editor => _editor;

    public KeymapProfileId ProfileId => _editor.ProfileId;

    public string Name => _editor.Name;

    public KeymapLayer Layer => _editor.Layer;

    public bool IsReadOnly { get; }

    public bool CanEditRows => !IsReadOnly;

    public bool IsApplicationLayer => Layer == KeymapLayer.Application;

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
            {
                RefreshRows();
                OnPropertyChanged(nameof(HasNoResults));
            }
        }
    }

    public bool IsDirty => _editor.IsDirty;

    public bool IsNew => _editor.ExpectedRevision is null;

    public bool CanSave => !IsReadOnly && _editor.CanSave && (IsDirty || IsNew);

    public int ConflictCount => _editor.Issues.Count(issue =>
        issue.Severity == KeymapIssueSeverity.Error);

    public int WarningCount => _editor.Issues.Count(issue =>
        issue.Severity == KeymapIssueSeverity.Warning);

    public bool HasConflicts => ConflictCount > 0;

    public bool HasNoResults => Rows.Count == 0;

    public bool HasPrefix => _editor.Prefix is not null;

    public bool CanEditPrefix => !IsReadOnly && Layer == KeymapLayer.Application;

    public string PrefixShortcut => _editor.Prefix is { } prefix
        ? KeySequenceDisplay.Format(prefix.Stroke)
        : "No prefix";

    public double PrefixTimeoutMilliseconds => _editor.Prefix?.Timeout.TotalMilliseconds ?? 750;

    public bool PrefixRepeatable => _editor.Prefix?.Repeatable ?? true;

    public FailedSequenceBehavior PrefixFailedBehavior =>
        _editor.Prefix?.FailedSequenceBehavior ?? FailedSequenceBehavior.DiscardAndShowHint;

    public string StateSummary => IsReadOnly
        ? "Built-in presets are read-only. Clone this preset to customize it."
        : HasConflicts
            ? $"Resolve {ConflictCount} blocking conflict(s) before saving."
            : IsDirty
                ? "Unsaved keybinding changes."
                : IsNew
                    ? "New keybinding profile has not been saved."
                : "Saved keybinding profile.";

    public void RecordShortcut(
        KeybindingEditorRowId rowId,
        IReadOnlyList<KeyStroke> strokes)
    {
        EnsureWritable();
        _editor.RecordShortcut(rowId, strokes);
    }

    public void Unbind(KeybindingEditorRowId rowId)
    {
        EnsureWritable();
        _editor.Unbind(rowId);
    }

    public void ResetShortcut(KeybindingEditorRowId rowId)
    {
        EnsureWritable();
        _editor.ResetShortcut(rowId);
    }

    public void ResetAll()
    {
        EnsureWritable();
        _editor.ResetBindingsAndPrefix();
    }

    public void RecordPrefix(KeyStroke stroke)
    {
        EnsurePrefixWritable();
        _editor.SetPrefix(new PrefixConfiguration(
            stroke,
            TimeSpan.FromMilliseconds(PrefixTimeoutMilliseconds),
            PrefixRepeatable,
            PrefixFailedBehavior));
    }

    public void UpdatePrefixOptions(
        double timeoutMilliseconds,
        bool repeatable,
        FailedSequenceBehavior failedBehavior)
    {
        EnsurePrefixWritable();
        if (_editor.Prefix is not { } prefix)
        {
            throw new InvalidOperationException("Record a prefix shortcut before editing its options.");
        }

        _editor.SetPrefix(new PrefixConfiguration(
            prefix.Stroke,
            TimeSpan.FromMilliseconds(timeoutMilliseconds),
            repeatable,
            failedBehavior));
    }

    public void ClearPrefix()
    {
        EnsurePrefixWritable();
        _editor.SetPrefix(null);
    }

    public KeybindingSettingsSaveRequest CreateSaveRequest()
    {
        EnsureWritable();
        return _editor.CreateSaveRequest();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _editor.Changed -= OnEditorChanged;
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Refresh();
    }

    private void Refresh()
    {
        RefreshRows();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(HasPrefix));
        OnPropertyChanged(nameof(PrefixShortcut));
        OnPropertyChanged(nameof(PrefixTimeoutMilliseconds));
        OnPropertyChanged(nameof(PrefixRepeatable));
        OnPropertyChanged(nameof(PrefixFailedBehavior));
        OnPropertyChanged(nameof(StateSummary));
    }

    private void RefreshRows()
    {
        var rows = _editor.Search(Query)
            .Select(row => new KeybindingEditorRowItemViewModel(row, !IsReadOnly));
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Clone the built-in preset before editing it.");
        }
    }

    private void EnsurePrefixWritable()
    {
        EnsureWritable();
        if (Layer != KeymapLayer.Application)
        {
            throw new InvalidOperationException("Only application keymaps can define a prefix.");
        }
    }
}
