using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.App.Views.Components;
using GhostShell.Core;

namespace GhostShell.App.Views;

/// <summary>
/// Edits one isolated workspace snapshot and reports host-owned save and close intents.
/// The control applies reversible in-editor operations itself; persistence, discard
/// confirmation, and switching to another workspace remain the containing window's.
/// </summary>
public sealed partial class WorkspaceEditorView : UserControl
{
    private WorkspaceEditorViewModel? _observedEditor;
    private bool _syncingPeers;
    private WorkspaceEntryEditorViewModel? _draggingEntry;

    public WorkspaceEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObserveEditor();
    }

    public WorkspaceEditorView(WorkspaceEditorViewModel editor)
        : this()
    {
        DataContext = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    /// <summary>Raised with a validated, immutable save request. The host owns persistence.</summary>
    public event EventHandler<WorkspaceEditorSaveRequestedEventArgs>? SaveRequested;

    /// <summary>Raised with the view model's close or confirm-discard disposition.</summary>
    public event EventHandler<WorkspaceEditorCancelRequestedEventArgs>? CancelRequested;

    /// <summary>Raised after the original workspace snapshot has been restored.</summary>
    public event EventHandler? ResetRequested;

    /// <summary>
    /// Asks the host to sample a colour from the screen. Screen sampling is a
    /// platform capability, so the editor requests it rather than owning it.
    /// </summary>
    public event EventHandler? PickAccentRequested;

    /// <summary>
    /// Raised when the rail asks for a different workspace. The editor holds one
    /// snapshot and cannot swap it for another, so the host decides what happens
    /// to the edits in progress before opening the next one.
    /// </summary>
    public event EventHandler<WorkspaceId>? WorkspaceSelectionRequested;

    /// <summary>Raised when the rail's plus asks for a workspace that does not exist yet.</summary>
    public event EventHandler? CreateWorkspaceRequested;

    /// <summary>
    /// Asks the platform host to start its workspace-isolation runtime installation flow.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? InstallWorkspaceIsolationRuntimeRequested;

    public event EventHandler<WorkspaceId>? RecreateWorkspaceIsolationRequested;

    public WorkspaceEditorViewModel? Editor => DataContext as WorkspaceEditorViewModel;

    public bool FocusInitialControl() =>
        this.FindControl<TextBox>("WorkspaceNameInput")!.Focus();

    private ListBox EntryListControl => this.FindControl<ListBox>("EntryList")!;

    private ListBox PeerListControl => this.FindControl<ListBox>("PeerList")!;

    private StackPanel SelectedEntryEditorControl =>
        this.FindControl<StackPanel>("SelectedEntryEditor")!;

    private void OnInstallWorkspaceIsolationRuntimeClick(object? sender, RoutedEventArgs e) =>
        InstallWorkspaceIsolationRuntimeRequested?.Invoke(sender, e);

    private void OnRecreateWorkspaceIsolationClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is { } editor)
        {
            RecreateWorkspaceIsolationRequested?.Invoke(this, editor.Id);
        }
    }

    private void ObserveEditor()
    {
        _observedEditor?.PropertyChanged -= OnEditorPropertyChanged;

        _observedEditor = Editor;
        _observedEditor?.PropertyChanged += OnEditorPropertyChanged;

        ConfigurePickers();
        SynchronizePeerSelection();
        // After the binding pass, not during DataContextChanged: the entry
        // list's ItemsSource has not delivered the new editor's entries yet, so
        // selecting the first entry now is coerced back to nothing — which is
        // why reopening the editor used to land on the empty state.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            EnsureEntrySelection();
            SynchronizePeerSelection();
        });
    }

    private void ConfigurePickers()
    {
        var editor = Editor;
        var entryConnectionPicker = this.FindControl<ComboBox>("EntryConnectionPicker")!;
        var entryScreenPicker = this.FindControl<ComboBox>("EntryScreenPicker")!;
        entryConnectionPicker.ItemsSource = editor?.ConnectionOptions;
        entryScreenPicker.ItemsSource = editor?.ScreenOptions;
    }

    /// <summary>
    /// Asks what the tab opens, then adds it. The dialog owns the choice and
    /// this owns applying it, so a cancelled dialog leaves the workspace
    /// untouched rather than half-added.
    /// </summary>
    private async void OnAddTabClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is not { } editor
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new AddWorkspaceTabDialog(
            editor.ConnectionOptions,
            editor.ScreenOptions,
            editor.LayoutOptions);
        if (await dialog.ShowDialog<WorkspaceTabSource?>(owner) is not { } source)
        {
            return;
        }

        CompleteAdd(source switch
        {
            WorkspaceTabSource.Connection connection => editor.AddConnection(connection.Id),
            WorkspaceTabSource.LinkedScreen screen => editor.AddSavedScreen(screen.Id),
            WorkspaceTabSource.CopiedScreen screen => editor.AddWorkspaceTabFromScreen(screen.Id),
            WorkspaceTabSource.NewScreen screen =>
                editor.AddWorkspaceTab(screen.LayoutId, screen.Name),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        });
    }

    /// <summary>
    /// Which picker asked the host to sample the screen. Screen sampling is a
    /// round trip through the window, and by the time the colour comes back the
    /// only thing that can say where it belongs is what asked for it.
    /// </summary>
    private SwatchPicker? _samplingPicker;

    private void OnIconChosen(object? sender, string icon)
    {
        _ = sender;
        if (Editor is { } editor)
        {
            editor.Icon = icon;
        }
    }

    private void OnSampleColorRequested(object? sender, EventArgs e)
    {
        _samplingPicker = sender as SwatchPicker;
        PickAccentRequested?.Invoke(this, e);
    }

    /// <summary>
    /// Applies a colour the host sampled from the screen to whichever picker
    /// asked for it.
    /// </summary>
    public void ApplySampledColor(Color color) => _samplingPicker?.ApplySampled(color);

    private void OnRenameWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = this.FindControl<TextBox>("WorkspaceNameInput")!;
        input.Focus();
        input.SelectAll();
    }

    private void OnCreateWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CreateWorkspaceRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAddIsolationMountClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Editor?.AddIsolationMount();
    }

    private void OnRemoveIsolationMountClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Editor is { } editor
            && sender is Control { DataContext: WorkspaceIsolationMountEditorViewModel mount })
        {
            editor.RemoveIsolationMount(mount);
        }
    }

    /// <summary>
    /// The host path is a folder on this machine, so the platform's folder
    /// picker names it — typing an absolute path is the fallback, not the way.
    /// </summary>
    private async void OnBrowseIsolationMountHostPathClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: WorkspaceIsolationMountEditorViewModel mount }
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return;
        }

        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a host folder",
            AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            mount.HostPath = path;
        }
    }

    private void SynchronizePeerSelection()
    {
        var editor = Editor;
        _syncingPeers = true;
        try
        {
            PeerListControl.SelectedItem = editor?.Peers.FirstOrDefault(peer => peer.IsCurrent);
        }
        finally
        {
            _syncingPeers = false;
        }
    }

    private void OnPeerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        if (_syncingPeers
            || Editor is not { } editor
            || (sender as ListBox)?.SelectedItem is not WorkspaceRailItemViewModel peer)
        {
            return;
        }

        if (peer.Id == editor.Id)
        {
            return;
        }

        WorkspaceSelectionRequested?.Invoke(this, peer.Id);
        // The host may refuse — an invalid workspace cannot be left behind — so
        // the rail shows what is actually open rather than what was clicked.
        Avalonia.Threading.Dispatcher.UIThread.Post(SynchronizePeerSelection);
    }

    /// <summary>
    /// Keeps the selection pointing at a row that still exists, and at nothing
    /// otherwise. Opening the editor selects nothing on purpose: a tab's panels
    /// are a second screenful, and unfolding them before being asked buries the
    /// list they belong to.
    /// </summary>
    private void EnsureEntrySelection()
    {
        var editor = Editor;
        if (EntryListControl.SelectedItem is not WorkspaceEntryEditorViewModel selected
            || editor is null
            || !editor.Entries.Contains(selected))
        {
            EntryListControl.SelectedItem = null;
        }

        ShowSelectedEntry(EntryListControl.SelectedItem as WorkspaceEntryEditorViewModel);
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (string.Equals(e.PropertyName, nameof(WorkspaceEditorViewModel.Entries), StringComparison.Ordinal))
        {
            EnsureEntrySelection();
        }
        else if (string.Equals(e.PropertyName, nameof(WorkspaceEditorViewModel.Peers), StringComparison.Ordinal))
        {
            SynchronizePeerSelection();
        }
    }

    private void OnEntrySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        ShowSelectedEntry((sender as ListBox)?.SelectedItem as WorkspaceEntryEditorViewModel);
    }

    private void ShowSelectedEntry(WorkspaceEntryEditorViewModel? entry)
    {
        SelectedEntryEditorControl.DataContext = entry;
        SelectedEntryEditorControl.IsVisible = entry is not null;
        // The prompt and the editor are the same slot: one of them is always
        // the answer to "what does clicking a row do".
        this.FindControl<TextBlock>("SelectTabPrompt")!.IsVisible =
            entry is null && Editor?.HasNoEntries == false;
    }

    /// <summary>
    /// Reordering is a drag on the handle, not a pair of arrows: the order is
    /// launch order, and dragging is how a list of things that happen in sequence
    /// is rearranged everywhere else. The move is applied continuously so the row
    /// under the cursor is the row that will be there when the drag ends.
    /// </summary>
    private void OnEntryDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: WorkspaceEntryEditorViewModel entry })
        {
            return;
        }

        _draggingEntry = entry;
        e.Pointer.Capture(sender as IInputElement);
        EntryListControl.SelectedItem = entry;
        e.Handled = true;
    }

    private void OnEntryDragHandleMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
        if (_draggingEntry is not { } entry || Editor is not { } editor)
        {
            return;
        }

        var target = EntryIndexAt(e.GetPosition(EntryListControl));
        if (target < 0 || target == editor.Entries.IndexOf(entry))
        {
            return;
        }

        _ = editor.MoveEntry(entry.Id, target);
        EntryListControl.SelectedItem = entry;
    }

    private void OnEntryDragHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        _draggingEntry = null;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Which row a point in the list belongs to. Containers are asked for their
    /// own bounds rather than the point being hit-tested, because the pointer is
    /// captured by the handle while dragging and hit-testing would only ever
    /// return the handle.
    /// </summary>
    private int EntryIndexAt(Point position)
    {
        for (var index = 0; index < EntryListControl.ItemCount; index++)
        {
            if (EntryListControl.ContainerFromIndex(index) is not Control container
                || container.TranslatePoint(default, EntryListControl) is not { } origin)
            {
                continue;
            }

            if (position.Y >= origin.Y && position.Y <= origin.Y + container.Bounds.Height)
            {
                return index;
            }
        }

        return -1;
    }





    private void CompleteAdd(WorkspaceEditorOperationResult result)
    {
        if (!result.IsSuccess || result.EntryId is not { } entryId || Editor is not { } editor)
        {
            ShowInteractionError(result.Error ?? "The workspace item could not be added.");
            return;
        }

        ClearInteractionError();
        EntryListControl.SelectedItem = editor.Entries.Single(entry => entry.Id == entryId);
        EntryListControl.ScrollIntoView(EntryListControl.SelectedItem!);
    }

    private void OnRemoveEntryClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: WorkspaceEntryEditorViewModel entry }
            || Editor is not { } editor)
        {
            return;
        }

        var index = editor.Entries.IndexOf(entry);
        var result = editor.RemoveEntry(entry.Id);
        if (!result.IsSuccess)
        {
            ShowInteractionError(result.Error ?? "The workspace item could not be removed.");
            return;
        }

        ClearInteractionError();
        EntryListControl.SelectedIndex = Math.Min(index, editor.Entries.Count - 1);
        EnsureEntrySelection();
    }

    private void OnAddTerminalPanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.Terminal);

    private void OnAddFilePanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.FileViewer);

    private void OnAddBrowserPanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.Browser);

    private void OnAddStatisticsPanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.Statistics);

    private void OnAddProcessMonitorPanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.ProcessMonitor);

    private void OnAddDatabasePanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.DatabaseViewer);

    private void OnAddDockerPanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.Docker);

    private void OnAddGitPanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.Git);

    private void AddPanel(object? sender, RoutedEventArgs e, ScreenPanelKind kind)
    {
        _ = e;
        if (sender is not Control { DataContext: WorkspaceTabEditorViewModel tab })
        {
            return;
        }

        if (!tab.AddPanel(kind))
        {
            ShowInteractionError("The selected layout has no unused slot for another panel.");
            return;
        }

        ClearInteractionError();
    }

    private void OnMovePanelEarlierClick(object? sender, RoutedEventArgs e) =>
        MovePanel(sender, e, -1);

    private void OnMovePanelLaterClick(object? sender, RoutedEventArgs e) =>
        MovePanel(sender, e, 1);

    private void MovePanel(object? sender, RoutedEventArgs e, int offset)
    {
        _ = e;
        if (sender is not Control { DataContext: WorkspaceTabPanelEditorViewModel panel }
            || EntryListControl.SelectedItem is not WorkspaceEntryEditorViewModel { Tab: { } tab })
        {
            return;
        }

        var destination = tab.Panels.IndexOf(panel) + offset;
        if (!tab.MovePanel(panel.Id, destination))
        {
            ShowInteractionError(offset < 0
                ? "The panel is already first."
                : "The panel is already last.");
            return;
        }

        ClearInteractionError();
    }

    private void OnRemovePanelClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: WorkspaceTabPanelEditorViewModel panel }
            || EntryListControl.SelectedItem is not WorkspaceEntryEditorViewModel { Tab: { } tab })
        {
            return;
        }

        if (!tab.RemovePanel(panel.Id))
        {
            ShowInteractionError("The panel is no longer part of this tab.");
            return;
        }

        ClearInteractionError();
    }

    private void OnClearOperationErrorClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Editor?.ClearOperationError();
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is not { } editor)
        {
            return;
        }

        editor.Reset();
        ClearInteractionError();
        EnsureEntrySelection();
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is { } editor)
        {
            CancelRequested?.Invoke(
                this,
                new WorkspaceEditorCancelRequestedEventArgs(editor.RequestCancel()));
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is not { } editor)
        {
            return;
        }

        try
        {
            var request = editor.CreateSaveRequest();
            ClearInteractionError();
            SaveRequested?.Invoke(this, new WorkspaceEditorSaveRequestedEventArgs(request));
        }
        catch (InvalidOperationException exception)
        {
            ShowInteractionError(exception.Message);
        }
    }

    private void ShowInteractionError(string message)
    {
        this.FindControl<TextBlock>("InteractionErrorText")!.Text = message;
        this.FindControl<Callout>("InteractionErrorCard")!.IsVisible = true;
    }

    private void ClearInteractionError()
    {
        this.FindControl<TextBlock>("InteractionErrorText")!.Text = string.Empty;
        this.FindControl<Callout>("InteractionErrorCard")!.IsVisible = false;
    }
}
