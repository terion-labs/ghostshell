using System.Collections.ObjectModel;
using System.ComponentModel;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns the self-contained tab definition stored directly in a workspace. Unlike a
/// saved-screen reference, later screen edits cannot affect this panel snapshot.
/// </summary>
public sealed class WorkspaceTabEditorViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<ScreenConnectionOption> _connectionOptions;
    private readonly IReadOnlyList<ScreenFileProviderOption> _fileProviderOptions;
    private readonly ObservableCollection<WorkspaceTabPanelEditorViewModel> _panels = [];
    private readonly ReadOnlyObservableCollection<WorkspaceTabPanelEditorViewModel> _readOnlyPanels;
    private string _name;
    private WorkspaceLayoutOption _selectedLayout;
    private bool _disposed;

    public WorkspaceTabEditorViewModel(
        WorkspaceEntry.Tab tab,
        IReadOnlyList<WorkspaceLayoutOption> layoutOptions,
        IReadOnlyList<ScreenConnectionOption> connectionOptions,
        IReadOnlyList<ScreenFileProviderOption> fileProviderOptions)
    {
        ArgumentNullException.ThrowIfNull(tab);
        LayoutOptions = layoutOptions ?? throw new ArgumentNullException(nameof(layoutOptions));
        _connectionOptions = connectionOptions ?? throw new ArgumentNullException(nameof(connectionOptions));
        _fileProviderOptions = fileProviderOptions
            ?? throw new ArgumentNullException(nameof(fileProviderOptions));
        Id = tab.Id;
        _name = tab.Name;
        _selectedLayout = LayoutOptions.Single(option => option.Id == tab.LayoutId);
        _readOnlyPanels = new(_panels);
        foreach (var panel in tab.Panels)
        {
            AddPanelEditor(new WorkspaceTabPanelEditorViewModel(
                panel,
                _selectedLayout,
                _connectionOptions,
                _fileProviderOptions));
        }
    }

    public WorkspaceEntryId Id { get; }

    public IReadOnlyList<WorkspaceLayoutOption> LayoutOptions { get; }

    public ReadOnlyObservableCollection<WorkspaceTabPanelEditorViewModel> Panels => _readOnlyPanels;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                PublishState();
            }
        }
    }

    public WorkspaceLayoutOption SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref _selectedLayout, value))
            {
                return;
            }

            foreach (var panel in _panels)
            {
                panel.UpdateLayout(value);
            }

            PublishState();
        }
    }

    public bool HasMissingLayout => !SelectedLayout.IsAvailable;

    public int MissingReferenceCount =>
        (HasMissingLayout ? 1 : 0) + _panels.Count(panel => panel.HasMissingDefinition);

    public bool HasMissingDefinition => MissingReferenceCount > 0;

    public bool CanAddPanel => SelectedLayout.Definition is { } layout
        && layout.Slots.Any(slot => _panels.All(panel => panel.SelectedSlot?.Id != slot.Id));

    public bool AddPanel(ScreenPanelKind kind)
    {
        if (!Enum.IsDefined(kind) || SelectedLayout.Definition is not { } layout)
        {
            return false;
        }

        var slot = layout.Slots.FirstOrDefault(candidate =>
            _panels.All(panel => panel.SelectedSlot?.Id != candidate.Id));
        if (slot is null)
        {
            return false;
        }

        var connectionId = kind == ScreenPanelKind.Terminal
            ? _connectionOptions.FirstOrDefault(option => option.IsAvailable)?.Id
            : null;
        var fileProviderId = kind == ScreenPanelKind.FileViewer
            ? _fileProviderOptions.FirstOrDefault(option =>
                option.IsAvailable && string.Equals(option.Id.Value, "builtin.files.home", StringComparison.Ordinal))?.Id
            : null;
        var panel = new ScreenPanelDefinition(
            ScreenPanelId.New(),
            slot.Id,
            kind,
            kind.ToString(),
            connectionId,
            PanelStartupBehavior.None,
            fileProviderId);
        AddPanelEditor(new WorkspaceTabPanelEditorViewModel(
            panel,
            SelectedLayout,
            _connectionOptions,
            _fileProviderOptions));
        PublishState();
        return true;
    }

    public bool RemovePanel(ScreenPanelId panelId)
    {
        var panel = _panels.SingleOrDefault(item => item.Id == panelId);
        if (panel is null)
        {
            return false;
        }

        panel.PropertyChanged -= OnPanelChanged;
        _panels.Remove(panel);
        PublishState();
        return true;
    }

    public bool MovePanel(ScreenPanelId panelId, int destinationIndex)
    {
        var sourceIndex = _panels
            .Select((panel, index) => (panel, index))
            .Where(item => item.panel.Id == panelId)
            .Select(item => item.index)
            .SingleOrDefault(-1);
        if (sourceIndex < 0 || destinationIndex < 0 || destinationIndex >= _panels.Count)
        {
            return false;
        }

        _panels.Move(sourceIndex, destinationIndex);
        PublishState();
        return true;
    }

    internal WorkspaceEntry.Tab Build() => new(
        Id,
        (Name ?? string.Empty).Trim(),
        SelectedLayout.Id,
        [.. _panels.Select(panel => panel.Build())]);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var panel in _panels)
        {
            panel.PropertyChanged -= OnPanelChanged;
        }

        _disposed = true;
    }

    private void AddPanelEditor(WorkspaceTabPanelEditorViewModel panel)
    {
        panel.PropertyChanged += OnPanelChanged;
        _panels.Add(panel);
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        PublishState();
    }

    private void PublishState()
    {
        OnPropertyChanged(nameof(HasMissingLayout));
        OnPropertyChanged(nameof(MissingReferenceCount));
        OnPropertyChanged(nameof(HasMissingDefinition));
        OnPropertyChanged(nameof(CanAddPanel));
    }
}
