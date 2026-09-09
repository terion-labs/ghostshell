using System.ComponentModel;
using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.RuntimePanels;

public sealed partial class DatabaseRuntimePanelView : UserControl
{
    private DatabaseRuntimePanelViewModel? _observedPanel;

    public DatabaseRuntimePanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObservePanel();
    }

    private void ObservePanel()
    {
        if (_observedPanel is not null)
        {
            _observedPanel.PasswordRequested -= OnPasswordRequested;
            _observedPanel.PropertyChanged -= OnPanelPropertyChanged;
        }

        _observedPanel = Panel;
        if (_observedPanel is not null)
        {
            _observedPanel.PasswordRequested += OnPasswordRequested;
            _observedPanel.PropertyChanged += OnPanelPropertyChanged;
        }

        SyncModeButtons();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is null
            or nameof(DatabaseRuntimePanelViewModel.SelectedMode)
            or nameof(DatabaseRuntimePanelViewModel.SelectedObject)
            or nameof(DatabaseRuntimePanelViewModel.SelectedDatabaseOverviewMode)
            or nameof(DatabaseRuntimePanelViewModel.IsDatabaseOverview))
        {
            SyncModeButtons();
        }
    }

    private void SyncModeButtons()
    {
        var panel = _observedPanel;
        DataModeButton.IsChecked = panel?.SelectedMode == DatabaseWorkspaceMode.Data;
        StructureModeButton.IsChecked = panel?.SelectedMode == DatabaseWorkspaceMode.Structure;
        IndexesModeButton.IsChecked = panel?.SelectedMode == DatabaseWorkspaceMode.Indexes;
        var hasObject = panel?.SelectedObject is not null;
        StructureModeButton.IsEnabled = hasObject;
        IndexesModeButton.IsEnabled = hasObject;
        ObjectsOverviewButton.IsChecked = panel?.IsDatabaseObjectsOverview == true;
        DiagramOverviewButton.IsChecked = panel?.IsDatabaseDiagramOverview == true;
    }

    private void OnDataModeClick(object? sender, RoutedEventArgs e) =>
        SetMode(DatabaseWorkspaceMode.Data);

    private void OnStructureModeClick(object? sender, RoutedEventArgs e) =>
        SetMode(DatabaseWorkspaceMode.Structure);

    private void OnIndexesModeClick(object? sender, RoutedEventArgs e) =>
        SetMode(DatabaseWorkspaceMode.Indexes);

    private void OnObjectsOverviewClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Panel?.ShowDatabaseOverview();
        SyncModeButtons();
    }

    private async void OnDiagramOverviewClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { } panel)
        {
            await panel.ShowDatabaseDiagramAsync();
        }

        SyncModeButtons();
        if (Panel?.IsDatabaseDiagramOverview == true)
        {
            DatabaseWorkspace.FocusDiagram();
        }
    }

    private void SetMode(DatabaseWorkspaceMode mode)
    {
        Panel?.SetMode(mode);
        // Clicking an already-selected ToggleButton tries to uncheck it. The
        // workspace mode is exclusive, so restore all three from source state.
        SyncModeButtons();
    }

    private async void OnPasswordRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is not { } panel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new DatabasePasswordPromptDialog(
            panel.SavedConnectionName ?? "Database",
            panel.CanStorePassword,
            panel.PasswordStoreLabel);
        var result = await dialog.ShowDialog<DatabasePasswordPromptResult?>(owner);
        if (result is not null)
        {
            panel.SetSessionPassword(result.Password);
            if (result.SaveToCredentialStore)
            {
                _ = await panel.StoreSessionPasswordAsync(result.Password);
            }

            await panel.ConnectAsync();
        }
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<PanelConnectionSelectedEventArgs>? ConnectionSelected;

    public event EventHandler<RoutedEventArgs>? NewConnectionRequested;

    /// <summary>The panel asks the shell to open its connection in the editor.</summary>
    public event EventHandler<RoutedEventArgs>? EditConnectionRequested;

    /// <summary>An object wants its own tab on the same connection.</summary>
    public event EventHandler<Asura.Application.DatabaseTableDescriptor>?
        OpenObjectInTabRequested;

    /// <summary>An object wants a panel split beside this one.</summary>
    public event EventHandler<Asura.Application.DatabaseTableDescriptor>?
        OpenObjectInPanelRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    private DatabaseRuntimePanelViewModel? Panel => DataContext as DatabaseRuntimePanelViewModel;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(sender, e);
    }

    private void OnConnectionSelected(object? sender, PanelConnectionSelectedEventArgs e) =>
        ConnectionSelected?.Invoke(this, e);

    private void OnNewConnectionRequested(object? sender, RoutedEventArgs e) =>
        NewConnectionRequested?.Invoke(sender, e);

    private void OnEditConnectionClick(object? sender, RoutedEventArgs e) =>
        EditConnectionRequested?.Invoke(this, e);

    private void OnOpenObjectInTab(
        object? sender,
        Asura.Application.DatabaseTableDescriptor e) =>
        OpenObjectInTabRequested?.Invoke(this, e);

    private void OnOpenObjectInPanel(
        object? sender,
        Asura.Application.DatabaseTableDescriptor e) =>
        OpenObjectInPanelRequested?.Invoke(this, e);

    private void OnSplitRequested(object? sender, PanelSplitOrientation orientation) =>
        SplitRequested?.Invoke(sender, orientation);
}
