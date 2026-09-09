using System.ComponentModel;
using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Asura.Application;
using Asura.Docker;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Views.RuntimePanels;

public sealed partial class DockerRuntimePanelView : UserControl
{
    private const double CompactLayoutMinimumWidth = 1_100;
    private const double CompactDetailHeaderMinimumWidth = 640;
    private const double LogPagingThreshold = 80;
    private const int LogScrollSettlePasses = 3;
    private static readonly FilePickerFileType LogFileType = new("Container logs")
    {
        Patterns = ["*.log", "*.txt"],
        MimeTypes = ["text/plain"],
    };
    private ScrollViewer? _logScrollViewer;
    private DockerRuntimePanelViewModel? _observedViewModel;
    private bool _isLoadingOlderLogs;
    private bool _isScrollingLogsToEnd;
    private bool _logScrollToEndPending;
    private bool _hasPerformedInitialLogScroll;
    private int _logScrollGeneration;

    public DockerRuntimePanelView()
    {
        InitializeComponent();
        DockerDetailHeader.SizeChanged += OnDetailHeaderSizeChanged;
        LogList.AddHandler(
            PointerWheelChangedEvent,
            OnLogPointerWheelChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        ObserveViewModel(ViewModel);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        ApplyLayoutClass(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyLayoutClass(e.NewSize.Width);
    }

    private void ApplyLayoutClass(double width)
    {
        var isCompact = width < CompactLayoutMinimumWidth;
        if (Classes.Contains("narrowPanel") != isCompact)
        {
            Classes.Set("narrowPanel", isCompact);
            InvalidateMeasure();
        }
    }

    private void OnDetailHeaderSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _ = sender;
        var compact = e.NewSize.Width < CompactDetailHeaderMinimumWidth;
        if (DockerDetailHeader.Classes.Contains("compactDetails") != compact)
        {
            DockerDetailHeader.Classes.Set("compactDetails", compact);
        }
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<PanelConnectionSelectedEventArgs>? ConnectionSelected;

    public event EventHandler<RoutedEventArgs>? NewConnectionRequested;

    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    public event EventHandler<DockerRuntimePanelViewModel>? ShellRequested;

    public event EventHandler<DockerRuntimePanelViewModel>? InlineShellRequested;

    public event EventHandler<TerminalRuntimePanelViewModel>? InlineShellTrustHostKeyRequested;

    public event EventHandler<RoutedEventArgs>? DockerInstallGuideRequested;

    private DockerRuntimePanelViewModel? ViewModel => DataContext as DockerRuntimePanelViewModel;

    private void OnDockerInstallGuideClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        DockerInstallGuideRequested?.Invoke(this, e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        ObserveViewModel(ViewModel);
    }

    private void ObserveViewModel(DockerRuntimePanelViewModel? viewModel)
    {
        _observedViewModel?.PropertyChanged -= OnViewModelPropertyChanged;

        _observedViewModel = viewModel;
        _logScrollGeneration++;
        _isScrollingLogsToEnd = false;
        _logScrollToEndPending = viewModel?.HasLogs == true;
        _hasPerformedInitialLogScroll = false;
        _observedViewModel?.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(AttachLogScrollViewer, DispatcherPriority.Loaded);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _ = sender;
        _ = e;
        _logScrollViewer?.ScrollChanged -= OnLogScrollChanged;
        _logScrollViewer = null;

        _logScrollGeneration++;
        _isScrollingLogsToEnd = false;
        _isLoadingOlderLogs = false;
        _logScrollToEndPending = _observedViewModel?.HasLogs == true;
        _hasPerformedInitialLogScroll = false;
    }

    private void AttachLogScrollViewer()
    {
        var scrollViewer = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (!ReferenceEquals(scrollViewer, _logScrollViewer))
        {
            _logScrollViewer?.ScrollChanged -= OnLogScrollChanged;

            _logScrollViewer = scrollViewer;
            _logScrollViewer?.ScrollChanged += OnLogScrollChanged;
        }

        if (!_hasPerformedInitialLogScroll && ViewModel is { HasLogs: true })
        {
            _logScrollToEndPending = true;
        }

        TryStartLogScrollToEnd();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(DockerRuntimePanelViewModel.LogScrollToEndRequest), StringComparison.Ordinal))
        {
            return;
        }

        _logScrollGeneration++;
        _isScrollingLogsToEnd = false;
        _logScrollToEndPending = true;
        _hasPerformedInitialLogScroll = false;
        Dispatcher.UIThread.Post(AttachLogScrollViewer, DispatcherPriority.Loaded);
    }

    private void TryStartLogScrollToEnd()
    {
        if (!_logScrollToEndPending
            || _isScrollingLogsToEnd
            || _logScrollViewer is not { } scrollViewer
            || ViewModel is not { HasLogs: true } panel)
        {
            return;
        }

        _isScrollingLogsToEnd = true;
        var generation = _logScrollGeneration;
        SettleLogScrollToEnd(panel, scrollViewer, generation, pass: 0);
    }

    private void SettleLogScrollToEnd(
        DockerRuntimePanelViewModel panel,
        ScrollViewer scrollViewer,
        int generation,
        int pass)
    {
        if (generation != _logScrollGeneration
            || !ReferenceEquals(panel, ViewModel)
            || !ReferenceEquals(scrollViewer, _logScrollViewer)
            || panel.LogRows.Count == 0)
        {
            if (generation == _logScrollGeneration)
            {
                _isScrollingLogsToEnd = false;
            }

            return;
        }

        LogList.ScrollIntoView(panel.LogRows[^1]);
        LogList.UpdateLayout();
        scrollViewer.ScrollToEnd();

        if (pass + 1 < LogScrollSettlePasses)
        {
            Dispatcher.UIThread.Post(
                () => SettleLogScrollToEnd(panel, scrollViewer, generation, pass + 1),
                DispatcherPriority.Loaded);
            return;
        }

        _logScrollToEndPending = false;
        _isScrollingLogsToEnd = false;
        _hasPerformedInitialLogScroll = true;
    }

    private async void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || ViewModel is not { } panel)
        {
            return;
        }

        var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var isAtEnd = scrollViewer.Offset.Y >= maximumOffset - 12;
        if (!_isScrollingLogsToEnd
            && _hasPerformedInitialLogScroll
            && panel.FollowLogs
            && !isAtEnd
            && e.OffsetDelta.Y < 0)
        {
            _logScrollGeneration++;
            _logScrollToEndPending = false;
            panel.FollowLogs = false;
        }

        await TryLoadOlderLogsAsync(panel, scrollViewer);
    }

    private void OnLogPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _ = sender;
        if (e.Delta.Y <= 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel is { } panel && _logScrollViewer is { } scrollViewer)
            {
                _ = TryLoadOlderLogsAsync(panel, scrollViewer);
            }
        }, DispatcherPriority.Background);
    }

    private async Task TryLoadOlderLogsAsync(
        DockerRuntimePanelViewModel panel,
        ScrollViewer scrollViewer)
    {
        if (!ReferenceEquals(panel, ViewModel)
            || !ReferenceEquals(scrollViewer, _logScrollViewer)
            || _isScrollingLogsToEnd
            || _logScrollToEndPending
            || !_hasPerformedInitialLogScroll
            || scrollViewer.Offset.Y > LogPagingThreshold
            || !panel.HasOlderLogs
            || panel.IsLoadingLogs
            || _isLoadingOlderLogs)
        {
            return;
        }

        _isLoadingOlderLogs = true;
        var previousExtent = scrollViewer.Extent.Height;
        var previousOffset = scrollViewer.Offset.Y;
        var restoreScheduled = false;
        try
        {
            if (!await panel.LoadOlderLogsAsync())
            {
                return;
            }

            restoreScheduled = true;
            RestoreLogViewportAfterPrepend(
                panel,
                scrollViewer,
                previousExtent,
                previousOffset,
                pass: 0);
        }
        finally
        {
            if (!restoreScheduled)
            {
                _isLoadingOlderLogs = false;
            }
        }
    }

    private void RestoreLogViewportAfterPrepend(
        DockerRuntimePanelViewModel panel,
        ScrollViewer scrollViewer,
        double previousExtent,
        double previousOffset,
        int pass)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(panel, ViewModel)
                || !ReferenceEquals(scrollViewer, _logScrollViewer))
            {
                _isLoadingOlderLogs = false;
                return;
            }

            LogList.UpdateLayout();
            var addedHeight = Math.Max(0, scrollViewer.Extent.Height - previousExtent);
            if (addedHeight <= 0 && pass + 1 < LogScrollSettlePasses)
            {
                RestoreLogViewportAfterPrepend(
                    panel,
                    scrollViewer,
                    previousExtent,
                    previousOffset,
                    pass + 1);
                return;
            }

            try
            {
                var maximumOffset = Math.Max(
                    0,
                    scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                scrollViewer.Offset = new Vector(
                    scrollViewer.Offset.X,
                    Math.Min(maximumOffset, previousOffset + addedHeight));
            }
            finally
            {
                _isLoadingOlderLogs = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnConnectionSelected(object? sender, PanelConnectionSelectedEventArgs e) =>
        ConnectionSelected?.Invoke(this, e);

    private void OnNewConnectionRequested(object? sender, RoutedEventArgs e) =>
        NewConnectionRequested?.Invoke(this, e);

    private void OnSplitRequested(object? sender, PanelSplitOrientation orientation) =>
        SplitRequested?.Invoke(sender, orientation);

    private void OnContainersClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectSection(DockerPanelSection.Containers);

    private void OnImagesClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectSection(DockerPanelSection.Images);

    private void OnVolumesClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectSection(DockerPanelSection.Volumes);

    private void OnNetworksClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectSection(DockerPanelSection.Networks);

    private void OnInfoClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectDetail(DockerPanelDetail.Info);

    private void OnLogsClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectDetail(DockerPanelDetail.Logs);

    private async void OnLogSearchClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } panel)
        {
            await panel.SearchLogsAsync();
        }
    }

    private async void OnClearLogSearchClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } panel)
        {
            await panel.ClearLogSearchAsync();
        }
    }

    private async void OnLogSearchKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Enter && ViewModel is { } panel)
        {
            e.Handled = true;
            await panel.SearchLogsAsync();
        }
    }

    private async void OnDownloadLogsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var panel = ViewModel;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (panel is null || storage?.CanSave != true)
        {
            return;
        }

        var destination = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Download complete container logs",
            SuggestedFileName = panel.LogDownloadFileName,
            DefaultExtension = "log",
            FileTypeChoices = [LogFileType],
            ShowOverwritePrompt = true,
        });
        if (destination is null)
        {
            return;
        }

        await using var stream = await destination.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.SetLength(0);
        }

        await panel.DownloadLogsAsync(stream);
    }

    private void OnStatsClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectDetail(DockerPanelDetail.Stats);

    private void OnShellClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is not { CanOpenShell: true } panel)
        {
            return;
        }

        panel.SelectDetail(DockerPanelDetail.Shell);
        RequestInlineShell(panel);
    }

    private void OnJsonClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectDetail(DockerPanelDetail.Json);

    private void OnFilesClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectDetail(DockerPanelDetail.Files);

    private void OnOpenShellClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } panel)
        {
            ShellRequested?.Invoke(this, panel);
        }
    }

    private void OnOpenInlineShellClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { CanOpenShell: true } panel)
        {
            RequestInlineShell(panel);
        }
    }

    private void OnContainerResourceClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: DockerResourceItemViewModel resource }
            || ViewModel is not { } panel)
        {
            return;
        }

        panel.SelectResource(resource);
        if (panel.IsShellDetail && panel.CanOpenShell)
        {
            RequestInlineShell(panel);
        }
    }

    private async void OnStartStackClick(object? sender, RoutedEventArgs e) =>
        await RunStackActionAsync(sender, e, DockerContainerAction.Start);

    private async void OnStopStackClick(object? sender, RoutedEventArgs e) =>
        await RunStackActionAsync(sender, e, DockerContainerAction.Stop);

    private async void OnRestartStackClick(object? sender, RoutedEventArgs e) =>
        await RunStackActionAsync(sender, e, DockerContainerAction.Restart);

    private async void OnPauseStackClick(object? sender, RoutedEventArgs e) =>
        await RunStackActionAsync(sender, e, DockerContainerAction.Pause);

    private async void OnResumeStackClick(object? sender, RoutedEventArgs e) =>
        await RunStackActionAsync(sender, e, DockerContainerAction.Resume);

    private void OnToggleStackClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: DockerContainerStackViewModel stack })
        {
            stack.IsExpanded = !stack.IsExpanded;
        }
    }

    private async Task RunStackActionAsync(
        object? sender,
        RoutedEventArgs e,
        DockerContainerAction action)
    {
        e.Handled = true;
        if (sender is Control { DataContext: DockerContainerStackViewModel stack }
            && ViewModel is { } panel)
        {
            await panel.RunStackActionAsync(stack, action);
        }
    }

    private void OnInlineShellTrustHostKeyRequested(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel?.InlineShell is { } shell)
        {
            InlineShellTrustHostKeyRequested?.Invoke(this, shell);
        }
    }

    private async void OnEmbeddedFileActionRequested(
        object? sender,
        FilePanelActionEventArgs e)
    {
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel })
        {
            return;
        }

        switch (e.Action)
        {
            case FilePanelAction.Open when panel.SelectedEntry is { } entry:
                await panel.OpenEntryAsync(entry);
                break;
            case FilePanelAction.Refresh:
                await panel.RefreshAsync();
                break;
            case FilePanelAction.CopyName:
                await CopyEmbeddedFileTextAsync(panel.SelectedEntry?.Name);
                break;
            case FilePanelAction.CopyPath:
                await CopyEmbeddedFileTextAsync(panel.SelectedEntryPath);
                break;
        }
    }

    private static void OnEmbeddedFileDismissOperationIssueRequested(
        object? sender,
        RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            panel.ClearOperationIssue();
        }
    }

    private static async void OnEmbeddedFileEntryDoubleTapped(
        object? sender,
        TappedEventArgs e)
    {
        _ = e;
        if (sender is ListBox
            {
                DataContext: FileRuntimePanelViewModel panel,
                SelectedItem: FileEntryViewModel entry,
            })
        {
            await panel.OpenEntryAsync(entry);
        }
    }

    private static async void OnEmbeddedFileEntrySelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        _ = e;
        if (sender is ListBox { DataContext: FileRuntimePanelViewModel panel } list)
        {
            panel.SetSelectedEntries(list.SelectedItems?
                .OfType<FileEntryViewModel>()
                .Select(item => item.Entry)
                .ToArray() ?? []);
            await panel.PreviewSelectedAsync();
        }
    }

    private static async void OnEmbeddedFileLocationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.NavigateFromTextAsync();
            e.Handled = true;
        }
    }

    private static async void OnEmbeddedFileNavigateUpRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.NavigateUpAsync();
        }
    }

    private static async void OnEmbeddedFileRefreshRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.RefreshAsync();
        }
    }

    private async Task CopyEmbeddedFileTextAsync(string? text)
    {
        if (!string.IsNullOrEmpty(text)
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void RequestInlineShell(DockerRuntimePanelViewModel panel)
    {
        if (!panel.HasInlineShell)
        {
            InlineShellRequested?.Invoke(this, panel);
        }
    }
}
