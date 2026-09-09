using System.ComponentModel;
using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Asura.Application;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using FluentIcons.Common;

namespace Asura.App.Views.RuntimePanels;

public sealed partial class FileRuntimePanelView : UserControl
{
    public static readonly StyledProperty<bool> IsEmbeddedProperty =
        AvaloniaProperty.Register<FileRuntimePanelView, bool>(nameof(IsEmbedded));

    private const double FileDragThreshold = 6;
    private static readonly DataFormat<FilePanelTransferPayload> FileDragFormat =
        DataFormat.CreateInProcessFormat<FilePanelTransferPayload>(
            "sh.asura.file-entry");
    private FileRuntimePanelViewModel? _boundPanel;
    private ActiveFileDrag? _activeFileDrag;
    private FileRuntimePanelView? _activeFileDropView;
    private FileDragCandidate? _fileDragCandidate;
    private ListBoxItem? _folderDropTarget;

    public FileRuntimePanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObservePanel();
        AddHandler(
            KeyDownEvent,
            OnPanelKeyDownTunnel,
            RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(KeyDownEvent, OnFilePanelKeyDown, RoutingStrategies.Tunnel);
        AddHandler(
            PointerPressedEvent,
            OnFilePointerPressed,
            RoutingStrategies.Tunnel);
        AddHandler(
            PointerMovedEvent,
            OnFilePointerMoved,
            RoutingStrategies.Tunnel);
        AddHandler(
            PointerReleasedEvent,
            OnFilePointerReleased,
            RoutingStrategies.Tunnel);
        PointerCaptureLost += OnFilePointerCaptureLost;
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
    }

    /// <summary>
    /// Hosts the complete File Viewer content inside another panel without a
    /// second panel header. It does not select a smaller or alternate browser.
    /// </summary>
    public bool IsEmbedded
    {
        get => GetValue(IsEmbeddedProperty);
        set => SetValue(IsEmbeddedProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    /// <summary>
    /// One of the file actions was asked for, from wherever it was shown: a
    /// toolbar button, the overflow menu, or a right-click.
    /// </summary>
    public event EventHandler<FilePanelActionEventArgs>? ActionRequested;

    public event EventHandler<PanelConnectionSelectedEventArgs>? ConnectionSelected;

    public event EventHandler<RoutedEventArgs>? NewConnectionRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    public event EventHandler<RoutedEventArgs>? DismissOperationIssueRequested;

    public event EventHandler<TappedEventArgs>? EntryDoubleTapped;

    public event EventHandler<SelectionChangedEventArgs>? EntrySelectionChanged;

    public event EventHandler<FilePanelTransferKeyEventArgs>?
        EntryTransferKeyRequested;

    public event EventHandler<FilePanelTransferDropEventArgs>?
        EntryTransferDropRequested;

    public event EventHandler<KeyEventArgs>? LocationKeyDown;

    public event EventHandler<RoutedEventArgs>? NavigateUpRequested;

    public event EventHandler<RoutedEventArgs>? RefreshRequested;

    // The embedded database preview's requests, forwarded with the original
    // sender: its DataContext is the preview's database view model, which is
    // exactly what the shell's database handlers pattern-match on.
    public event EventHandler<Asura.Application.DatabaseTableDescriptor>?
        DatabaseObjectOpenInTabRequested;

    public event EventHandler<Asura.Application.DatabaseTableDescriptor>?
        DatabaseObjectOpenInPanelRequested;

    public event EventHandler<RoutedEventArgs>? DatabaseOpenInViewerRequested;

    private void OnDatabaseObjectOpenInTab(
        object? sender,
        Asura.Application.DatabaseTableDescriptor e) =>
        DatabaseObjectOpenInTabRequested?.Invoke(sender, e);

    private void OnDatabaseObjectOpenInPanel(
        object? sender,
        Asura.Application.DatabaseTableDescriptor e) =>
        DatabaseObjectOpenInPanelRequested?.Invoke(sender, e);

    private void OnDatabaseOpenInViewer(object? sender, RoutedEventArgs e) =>
        DatabaseOpenInViewerRequested?.Invoke(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnConnectionSelected(object? sender, PanelConnectionSelectedEventArgs e) =>
        ConnectionSelected?.Invoke(this, e);

    private void OnNewConnectionRequested(object? sender, RoutedEventArgs e) =>
        NewConnectionRequested?.Invoke(this, e);

    private void OnSplitRequested(object? sender, PanelSplitOrientation orientation) =>
        SplitRequested?.Invoke(sender, orientation);

    private void OnDismissOperationIssueClick(object? sender, RoutedEventArgs e) =>
        DismissOperationIssueRequested?.Invoke(sender, e);

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e) =>
        EntryDoubleTapped?.Invoke(sender, e);

    private void OnSortNameClick(object? sender, RoutedEventArgs e) =>
        _boundPanel?.ChangeSort(FileEntrySortField.Name);

    private void OnSortSizeClick(object? sender, RoutedEventArgs e) =>
        _boundPanel?.ChangeSort(FileEntrySortField.Size);

    private void OnSortModifiedClick(object? sender, RoutedEventArgs e) =>
        _boundPanel?.ChangeSort(FileEntrySortField.Modified);

    /// <summary>
    /// The browser showing the previewed page, created on first use and reused
    /// for later pages: an embedded Chromium renderer is expensive enough that
    /// creating one per selection would be visible.
    /// </summary>
    private BrowserRendererView? _htmlPreview;

    private void ObservePanel()
    {
        if (_boundPanel is { } previous)
        {
            previous.PropertyChanged -= OnPanelPropertyChanged;
            previous.ActionRequested -= OnPanelActionRequested;
        }

        ReleaseHtmlPreview();

        _boundPanel = DataContext as FileRuntimePanelViewModel;
        if (_boundPanel is { } panel)
        {
            panel.PropertyChanged += OnPanelPropertyChanged;
            panel.ActionRequested += OnPanelActionRequested;
        }

        RefreshOverflowMenu();
    }

    protected override void OnAttachedToVisualTree(
        VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_boundPanel?.HtmlAddress is not null)
        {
            ShowHtmlPreview();
        }
    }

    protected override void OnDetachedFromVisualTree(
        VisualTreeAttachmentEventArgs e)
    {
        ReleaseHtmlPreview();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The bridge from a menu row to the window. A file action needs a folder
    /// picker, a confirmation or a name typed into a dialog, all of which the
    /// window owns; the panel only says which action was asked for.
    /// </summary>
    private void OnPanelActionRequested(object? sender, FilePanelAction action)
    {
        _ = sender;
        ActionRequested?.Invoke(this, new FilePanelActionEventArgs(action));
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (string.Equals(e.PropertyName, nameof(FileRuntimePanelViewModel.HtmlAddress), StringComparison.Ordinal))
        {
            ShowHtmlPreview();
        }
        else if (string.Equals(e.PropertyName, nameof(FileRuntimePanelViewModel.MenuActions), StringComparison.Ordinal))
        {
            RefreshOverflowMenu();
        }
    }

    private void ShowHtmlPreview()
    {
        if (DataContext is not FileRuntimePanelViewModel { HtmlAddress: { } address })
        {
            return;
        }

        if (_htmlPreview is null)
        {
            var factory = (TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel)
                ?.BrowserRendererViewFactory;
            if (factory is null)
            {
                // Said rather than shown as an empty panel: without a browser
                // there is nothing to render, and silence reads as a bug.
                HtmlPreviewHost.Content = new TextBlock
                {
                    Classes = { "Muted" },
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = "Web pages cannot be previewed on this system.",
                };
                return;
            }

            _htmlPreview = factory.CreateIsolatedHtmlPreview();
            HtmlPreviewHost.Content = _htmlPreview.View;
        }

        _ = _htmlPreview.Renderer.NavigateAsync(address, CancellationToken.None).AsTask();
    }

    private void ReleaseHtmlPreview()
    {
        var preview = _htmlPreview;
        _htmlPreview = null;
        if (preview is null)
        {
            return;
        }

        if (ReferenceEquals(HtmlPreviewHost.Content, preview.View))
        {
            HtmlPreviewHost.Content = null;
        }

        preview.Dispose();
    }

    private void OnPdfPageBackClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is FileRuntimePanelViewModel panel)
        {
            _ = panel.TurnPdfPageAsync(-1);
        }
    }

    private void OnPdfPageForwardClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is FileRuntimePanelViewModel panel)
        {
            _ = panel.TurnPdfPageAsync(1);
        }
    }

    private void OnPreviewDownloadClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RequestDeferredPreview();
    }

    /// <summary>
    /// Space asks for a waiting preview from anywhere in the panel: the file
    /// list keeps focus while browsing, so requiring the button to be focused
    /// would make the shortcut useless exactly when it is wanted.
    ///
    /// It must tunnel rather than bubble. A ListBox treats Space as a selection
    /// key and marks it handled, so by the time the event would reach this
    /// panel on the way up, it is already spent.
    /// </summary>
    private void OnPanelKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Space
            || e.Handled
            || DataContext is not FileRuntimePanelViewModel { ShowPreviewDownloadPrompt: true })
        {
            return;
        }

        // Space is a character wherever text is being typed, and a shortcut
        // only outside those.
        if (e.Source is TextBox || IsWithinTextInput(e.Source as Visual))
        {
            return;
        }

        e.Handled = true;
        RequestDeferredPreview();
    }

    private static bool IsWithinTextInput(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TextBox)
            {
                return true;
            }
        }

        return false;
    }

    private void RequestDeferredPreview()
    {
        if (DataContext is FileRuntimePanelViewModel panel)
        {
            _ = panel.PreviewDeferredAsync();
        }
    }

    private void OnEntrySelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        EntrySelectionChanged?.Invoke(sender, e);

    private void OnLocationKeyDown(object? sender, KeyEventArgs e) =>
        LocationKeyDown?.Invoke(sender, e);

    private void OnNavigateUpClick(object? sender, RoutedEventArgs e) =>
        NavigateUpRequested?.Invoke(sender, e);

    private void OnRefreshClick(object? sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(sender, e);

    private void OnTogglePreviewClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is FileRuntimePanelViewModel panel)
        {
            panel.IsPreviewVisible = !panel.IsPreviewVisible;
        }
    }

    private void OnFilePanelKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Source is not Control source
            || FindFileList(source) is not { } list
            || !HasTransferModifier(e.KeyModifiers)
            || e.Key is not (Key.C or Key.X or Key.V))
        {
            return;
        }

        var entries = SelectedEntries(list);
        if (e.Key is Key.C or Key.X && entries.Count == 0)
        {
            return;
        }

        EntryTransferKeyRequested?.Invoke(
            list,
            new FilePanelTransferKeyEventArgs(entries, e));
    }

    /// <summary>
    /// Whether the last right-press landed on a file or on the folder behind
    /// the listing. It decides which menu opens, and it is read here rather
    /// than at opening time because by then the pointer has moved on.
    /// </summary>
    private bool _contextMenuTargetsEntry;

    private void OnFilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            NoteContextMenuTarget(e.Source);
            return;
        }

        if (e.Source is not Control source
            || FindFileList(source) is not { } list
            || source.FindAncestorOfType<ListBoxItem>()?.DataContext
                is not FileEntryViewModel entry)
        {
            return;
        }

        var point = e.GetCurrentPoint(list);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _fileDragCandidate = new FileDragCandidate(
            list,
            entry.Entry,
            point.Position,
            e.Pointer);
    }

    /// <summary>
    /// A right-click on a row that is not part of the selection acts on that
    /// row, the way every file manager does. One inside the selection leaves it
    /// alone, so a menu opened over six selected files still means all six.
    /// </summary>
    private void NoteContextMenuTarget(object? source)
    {
        var row = source as ListBoxItem
            ?? (source as Control)?.FindAncestorOfType<ListBoxItem>();
        _contextMenuTargetsEntry = row is not null;
        if (row is { IsSelected: false, DataContext: FileEntryViewModel entry }
            && FindFileList(row) is { } list)
        {
            list.SelectedItem = entry;
        }
    }

    private void OnFileContextMenuOpening(object? sender, CancelEventArgs e)
    {
        _ = sender;
        if (DataContext is not FileRuntimePanelViewModel panel)
        {
            e.Cancel = true;
            return;
        }

        var actions = _contextMenuTargetsEntry
            ? panel.EntryMenuActions
            : panel.FolderMenuActions;
        // A menu with nothing in it is a flash of empty chrome, which reads as
        // a fault rather than as "this connection offers nothing here".
        e.Cancel = actions.Count == 0;
        FileListContextMenu.ItemsSource = WithGroupRules(actions);
    }

    /// <summary>
    /// The actions with a rule drawn where the kind of action changes. A rule
    /// in a menu is a control rather than a row of data, so the panel hands
    /// over the actions and their grouping, and the view puts the rules in.
    /// </summary>
    private static IReadOnlyList<object> WithGroupRules(
        IReadOnlyList<FileActionViewModel> actions)
    {
        var items = new List<object>(actions.Count);
        foreach (var action in actions)
        {
            if (action.StartsGroup)
            {
                items.Add(new Separator());
            }

            items.Add(action);
        }

        return items;
    }

    /// <summary>
    /// The overflow menu's rows, set rather than bound: its rules are controls,
    /// and the button's flyout is outside the name scope the markup can bind
    /// into anyway.
    /// </summary>
    private void RefreshOverflowMenu()
    {
        if (FileActionsOverflowButton.Flyout is MenuFlyout menu)
        {
            menu.ItemsSource = DataContext is FileRuntimePanelViewModel panel
                ? WithGroupRules(panel.MenuActions)
                : null;
        }
    }

    private void OnFilePointerMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
        if (_activeFileDrag is { } active
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            var current = e.GetCurrentPoint(this);
            if (!current.Properties.IsLeftButtonPressed)
            {
                CancelActiveFileDrag(active.Pointer);
                return;
            }

            UpdateActiveFileDrag(e, active);
            e.Handled = true;
            return;
        }

        if (_fileDragCandidate is not { } candidate
            || !ReferenceEquals(e.Pointer, candidate.Pointer))
        {
            return;
        }

        var point = e.GetCurrentPoint(candidate.Source);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _fileDragCandidate = null;
            return;
        }

        var delta = point.Position - candidate.Origin;
        if (Math.Abs(delta.X) < FileDragThreshold
            && Math.Abs(delta.Y) < FileDragThreshold)
        {
            return;
        }

        _fileDragCandidate = null;
        var entries = SelectedEntries(candidate.Source);
        if (!entries.Contains(candidate.Entry))
        {
            entries = [candidate.Entry];
        }

        if (DataContext is not FileRuntimePanelViewModel panel)
        {
            return;
        }

        var payload = new FilePanelTransferPayload(
            panel.Id,
            entries,
            FilePanelTransferOperation.Copy);
        if (TopLevel.GetTopLevel(this) is not MainWindow window)
        {
            return;
        }

        var activeDrag = new ActiveFileDrag(
            candidate.Pointer,
            payload,
            CreateDragGhostPayload(entries, panel.ConnectionDisplayName));
        // Changing capture can synchronously raise capture-lost for the list
        // row. Establish the drag only after that old capture has unwound.
        candidate.Pointer.Capture(this);
        _activeFileDrag = activeDrag;
        window.ShowDragGhost(activeDrag.Ghost, e.GetPosition(window));
        UpdateActiveFileDrag(e, activeDrag);
        e.Handled = true;
    }

    private void OnFilePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        if (_activeFileDrag is { } active
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            CompleteActiveFileDrag(e, active);
            e.Handled = true;
            return;
        }

        _fileDragCandidate = null;
    }

    private void OnFilePointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs e)
    {
        _ = sender;
        if (_activeFileDrag is { } active
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            CancelActiveFileDrag(active.Pointer, releaseCapture: false);
        }
    }

    private void UpdateActiveFileDrag(
        PointerEventArgs e,
        ActiveFileDrag active)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window)
        {
            CancelActiveFileDrag(active.Pointer);
            return;
        }

        var position = e.GetPosition(window);
        window.MoveDragGhost(position);
        var target = ResolveInternalFileDropTarget(window, position, active.Payload);
        if (!ReferenceEquals(_activeFileDropView, target?.View))
        {
            _activeFileDropView?.ClearTransferDropTarget();
            _activeFileDropView = target?.View;
        }

        if (target is { } resolved)
        {
            resolved.View.SetTransferDropTarget(resolved.Target.Folder);
        }
        else
        {
            _activeFileDropView?.ClearTransferDropTarget();
            _activeFileDropView = null;
        }
    }

    private void CompleteActiveFileDrag(
        PointerReleasedEventArgs e,
        ActiveFileDrag active)
    {
        var window = TopLevel.GetTopLevel(this) as MainWindow;
        var target = window is null
            ? null
            : ResolveInternalFileDropTarget(
                window,
                e.GetPosition(window),
                active.Payload);

        _activeFileDrag = null;
        _fileDragCandidate = null;
        _activeFileDropView?.ClearTransferDropTarget();
        _activeFileDropView = null;
        active.Pointer.Capture(null);
        window?.HideDragGhost();

        if (target is not { } resolved)
        {
            return;
        }

        resolved.View.EntryTransferDropRequested?.Invoke(
            resolved.View,
            new FilePanelTransferDropEventArgs(
                resolved.Target.Panel,
                resolved.Target.Payload,
                resolved.Target.DestinationFolder));
    }

    private void CancelActiveFileDrag(
        IPointer pointer,
        bool releaseCapture = true)
    {
        _activeFileDrag = null;
        _fileDragCandidate = null;
        _activeFileDropView?.ClearTransferDropTarget();
        _activeFileDropView = null;
        if (releaseCapture)
        {
            pointer.Capture(null);
        }

        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.HideDragGhost();
        }
    }

    private static InternalFileDropTarget? ResolveInternalFileDropTarget(
        MainWindow window,
        Point position,
        FilePanelTransferPayload payload)
    {
        if (window.InputHitTest(position) is not Control hit)
        {
            return null;
        }

        var view = hit as FileRuntimePanelView
            ?? hit.FindAncestorOfType<FileRuntimePanelView>();
        var target = view?.ResolveFileDropTarget(hit, payload);
        return view is not null && target is not null
            ? new InternalFileDropTarget(view, target)
            : null;
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (ResolveFileDropTarget(e.Source, e.DataTransfer) is { } target)
        {
            SetTransferDropTarget(target.Folder);
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        ClearTransferDropTarget();
        e.DragEffects = DragDropEffects.None;
    }

    private void OnFileDragLeave(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (Bounds.Contains(e.GetPosition(this)))
        {
            return;
        }

        ClearTransferDropTarget();
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        _ = sender;
        var target = ResolveFileDropTarget(e.Source, e.DataTransfer);
        ClearTransferDropTarget();
        if (target is null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        EntryTransferDropRequested?.Invoke(
            this,
            new FilePanelTransferDropEventArgs(
                target.Panel,
                target.Payload,
                target.DestinationFolder));
    }

    private void SetTransferDropTarget(ListBoxItem? folder)
    {
        TransferDropOutline.IsVisible = folder is null;
        if (ReferenceEquals(_folderDropTarget, folder))
        {
            return;
        }

        _folderDropTarget?.Classes.Remove("transferDropTarget");
        _folderDropTarget = folder;
        _folderDropTarget?.Classes.Add("transferDropTarget");
    }

    private void ClearTransferDropTarget()
    {
        TransferDropOutline.IsVisible = false;
        _folderDropTarget?.Classes.Remove("transferDropTarget");
        _folderDropTarget = null;
    }

    private FileDropTarget? ResolveFileDropTarget(
        object? source,
        IDataTransfer dataTransfer)
    {
        return dataTransfer.TryGetValue(FileDragFormat) is { } payload
            ? ResolveFileDropTarget(source, payload)
            : null;
    }

    private FileDropTarget? ResolveFileDropTarget(
        object? source,
        FilePanelTransferPayload payload)
    {
        if (DataContext is not FileRuntimePanelViewModel panel
            || payload.Entries.Count == 0)
        {
            return null;
        }

        var folder = FindDirectoryDropTarget(source);
        var destinationFolder = folder?.DataContext is FileEntryViewModel folderEntry
            ? folderEntry.Entry.Location
            : null;

        // The panel background represents its current folder. Returning a drag
        // there to its source panel cannot change ownership or location.
        if (payload.SourcePanelId == panel.Id && destinationFolder is null)
        {
            return null;
        }

        return payload.Entries.All(entry =>
                panel.CanReceiveTransfer(entry, destinationFolder))
            ? new FileDropTarget(panel, payload, folder, destinationFolder)
            : null;
    }

    private static ListBoxItem? FindDirectoryDropTarget(object? source)
    {
        if (source is not Control control)
        {
            return null;
        }

        var item = control as ListBoxItem
            ?? control.FindAncestorOfType<ListBoxItem>();
        return item?.DataContext is FileEntryViewModel { IsDirectory: true }
            ? item
            : null;
    }

    private static ListBox? FindFileList(Control source) =>
        source as ListBox ?? source.FindAncestorOfType<ListBox>();

    private static IReadOnlyList<FilePanelEntry> SelectedEntries(ListBox list) =>
        list.SelectedItems?
            .OfType<FileEntryViewModel>()
            .Select(item => item.Entry)
            .ToArray()
        ?? [];

    private static bool HasTransferModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Meta)
        || modifiers.HasFlag(KeyModifiers.Control);

    private static DragGhostPayload CreateDragGhostPayload(
        IReadOnlyList<FilePanelEntry> entries,
        string source)
    {
        var title = entries.Count == 1
            ? entries[0].Name
            : $"{entries.Count} items";
        var symbol = entries.Count == 1
            && entries[0].Kind == FilePanelEntryKind.Directory
            ? Symbol.Folder
            : Symbol.DocumentMultiple;
        return new DragGhostPayload(symbol, title, $"From {source}");
    }

    private sealed record FileDropTarget(
        FileRuntimePanelViewModel Panel,
        FilePanelTransferPayload Payload,
        ListBoxItem? Folder,
        FilePanelLocation? DestinationFolder);

    private sealed record InternalFileDropTarget(
        FileRuntimePanelView View,
        FileDropTarget Target);

    private sealed record ActiveFileDrag(
        IPointer Pointer,
        FilePanelTransferPayload Payload,
        DragGhostPayload Ghost);

    private sealed record FileDragCandidate(
        ListBox Source,
        FilePanelEntry Entry,
        Point Origin,
        IPointer Pointer);
}

/// <summary>
/// Which file action was asked for. A routed-event argument so the window's
/// existing per-action handlers, which read the panel off the sender, can be
/// called with it unchanged.
/// </summary>
public sealed class FilePanelActionEventArgs(FilePanelAction action) : RoutedEventArgs
{
    public FilePanelAction Action { get; } = action;
}

public sealed record FilePanelTransferPayload(
    Asura.Core.PanelInstanceId SourcePanelId,
    IReadOnlyList<FilePanelEntry> Entries,
    FilePanelTransferOperation Operation);

public sealed class FilePanelTransferKeyEventArgs(
    IReadOnlyList<FilePanelEntry> entries,
    KeyEventArgs keyEvent) : EventArgs
{
    public IReadOnlyList<FilePanelEntry> Entries { get; } =
        entries ?? throw new ArgumentNullException(nameof(entries));

    public KeyEventArgs KeyEvent { get; } =
        keyEvent ?? throw new ArgumentNullException(nameof(keyEvent));
}

public sealed class FilePanelTransferDropEventArgs(
    FileRuntimePanelViewModel destination,
    FilePanelTransferPayload payload,
    FilePanelLocation? destinationFolder) : EventArgs
{
    public FileRuntimePanelViewModel Destination { get; } =
        destination ?? throw new ArgumentNullException(nameof(destination));

    public FilePanelTransferPayload Payload { get; } =
        payload ?? throw new ArgumentNullException(nameof(payload));

    public FilePanelLocation? DestinationFolder { get; } =
        destinationFolder;
}
