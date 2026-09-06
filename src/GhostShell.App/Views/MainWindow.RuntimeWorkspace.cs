using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.App.Views.Components;
using GhostShell.App.Views.RuntimePanels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Views;

public sealed partial class MainWindow
{
    private RuntimeTabDragController? _runtimeTabDrag;

    private RuntimeTabDragController RuntimeTabDrag => _runtimeTabDrag ??= new(
        this,
        ViewModel,
        new RuntimeTabDragPresentation(
            ShowDragGhost,
            MoveDragGhost,
            HideDragGhost),
        _lifetime.Token);

    public async Task RequestNewTerminalAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (ResolveNewTerminalTarget(ViewModel.HasRuntimeWorkspace)
            == NewTerminalTarget.ExistingRuntimeWorkspace)
        {
            ViewModel.ShowWorkspace();
            if (!ViewModel.IsWorkspaceVisible)
            {
                return;
            }

            if (await ViewModel.AddLocalTerminalTabAsync(_lifetime.Token))
            {
                FocusActivePanel();
            }
            return;
        }

        await OpenDefaultLocalTerminalAsync();
    }

    private async void OnSavedConnectionLaunchRequested(
        object? sender,
        SavedConnectionLaunchViewModel launch)
    {
        _ = sender;
        try
        {
            if (await ViewModel.AddSavedConnectionTabAsync(launch, _lifetime.Token))
            {
                FocusActivePanel();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnWorkspaceNetworkConnectionSelected(
        object? sender,
        WorkspaceNetworkConnectionOptionViewModel connection)
    {
        _ = sender;
        try
        {
            await ViewModel.WorkspaceNetwork.SelectAsync(connection.Id, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnWorkspaceNetworkToggleRequested(
        object? sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            await ViewModel.WorkspaceNetwork.ToggleAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    public async Task RequestNewFileViewerAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (!ViewModel.HasRuntimeWorkspace)
        {
            if (ViewModel.Workspaces.FirstOrDefault() is { } workspace)
            {
                await OpenRuntimeWorkspaceAsync(token =>
                    ViewModel.OpenWorkspaceAsync(workspace.Id, token));
            }
            else if (ViewModel.Connections.FirstOrDefault(item => item.CanOpen) is { } connection)
            {
                await OpenRuntimeWorkspaceAsync(token =>
                    ViewModel.OpenConnectionAsync(connection.Id, token));
            }
        }

        if (ViewModel.RuntimeWorkspace?.ActiveTab is null)
        {
            ViewModel.SetError("Open a workspace or connection before adding a File Viewer.");
            return;
        }

        if (await ViewModel.AddFilePanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    public Task RequestNewBrowserAsync() =>
        RequestNewBrowserAsync(profileId: null);

    public async Task RequestNewBrowserAsync(BrowserProfileId? profileId)
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (!ViewModel.HasRuntimeWorkspace)
        {
            await OpenRuntimeWorkspaceAsync(
                token => ViewModel.OpenLocalBrowserWorkspaceAsync(profileId, token));
            return;
        }

        ViewModel.ShowWorkspace();
        if (ViewModel.RuntimeWorkspace?.ActiveTab is null)
        {
            ViewModel.SetError("Open a workspace or connection before adding a browser.");
            return;
        }

        if (await ViewModel.AddBrowserPanelAsync(profileId, _lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    public async Task RequestNewDatabaseAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (!ViewModel.HasRuntimeWorkspace)
        {
            await OpenRuntimeWorkspaceAsync(
                ViewModel.OpenLocalDatabaseWorkspaceAsync);
            return;
        }

        ViewModel.ShowWorkspace();
        if (ViewModel.RuntimeWorkspace?.ActiveTab is null)
        {
            ViewModel.SetError("Open a workspace or connection before adding a database viewer.");
            return;
        }

        if (await ViewModel.AddDatabasePanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    public async Task RequestNewDockerAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (!ViewModel.HasRuntimeWorkspace)
        {
            if (ViewModel.Workspaces.FirstOrDefault() is { } workspace)
            {
                await OpenRuntimeWorkspaceAsync(token =>
                    ViewModel.OpenWorkspaceAsync(workspace.Id, token));
            }
            else
            {
                await OpenDefaultLocalTerminalAsync();
            }
        }

        if (ViewModel.RuntimeWorkspace?.ActiveTab is null)
        {
            ViewModel.SetError("Open a workspace before adding a Docker panel.");
            return;
        }

        if (await ViewModel.AddDockerPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    public async Task RequestNewGitAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (!ViewModel.HasRuntimeWorkspace)
        {
            if (ViewModel.Workspaces.FirstOrDefault() is { } workspace)
            {
                await OpenRuntimeWorkspaceAsync(token =>
                    ViewModel.OpenWorkspaceAsync(workspace.Id, token));
            }
            else
            {
                await OpenDefaultLocalTerminalAsync();
            }
        }

        if (ViewModel.RuntimeWorkspace?.ActiveTab is null)
        {
            ViewModel.SetError("Open a workspace before adding a Git panel.");
            return;
        }

        if (await ViewModel.AddGitPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    public Task RequestNewStatisticsAsync() =>
        RequestNewMonitorAsync(PanelKind.Statistics);

    public Task RequestNewProcessMonitorAsync() =>
        RequestNewMonitorAsync(PanelKind.ProcessMonitor);

    private async Task RequestNewAdapterTabAsync(PanelKind kind)
    {
        if (kind is not (PanelKind.Browser
            or PanelKind.FileViewer
            or PanelKind.Statistics
            or PanelKind.ProcessMonitor
            or PanelKind.DatabaseViewer
            or PanelKind.Docker
            or PanelKind.Git))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        // With no runtime workspace, the existing session-start paths create the
        // first workspace and tab. Once a tab exists, the New Tab host must append
        // another tab; only a placed placeholder may add to the active tab.
        if (!ViewModel.HasRuntimeWorkspace)
        {
            await RequestNewAdapterSessionAsync(kind);
            return;
        }

        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        ViewModel.ShowWorkspace();
        var added = kind switch
        {
            PanelKind.Browser =>
                await ViewModel.AddBrowserTabAsync(_lifetime.Token),
            PanelKind.FileViewer =>
                await ViewModel.AddFileViewerTabAsync(_lifetime.Token),
            PanelKind.Statistics =>
                await ViewModel.AddStatisticsTabAsync(_lifetime.Token),
            PanelKind.ProcessMonitor =>
                await ViewModel.AddProcessMonitorTabAsync(_lifetime.Token),
            PanelKind.DatabaseViewer =>
                await ViewModel.AddDatabaseTabAsync(_lifetime.Token),
            PanelKind.Docker =>
                await ViewModel.AddDockerTabAsync(_lifetime.Token),
            PanelKind.Git =>
                await ViewModel.AddGitTabAsync(_lifetime.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        if (added)
        {
            FocusActivePanel();
        }
    }

    private Task RequestNewAdapterSessionAsync(PanelKind kind) => kind switch
    {
        PanelKind.Browser => RequestNewBrowserAsync(),
        PanelKind.FileViewer => RequestNewFileViewerAsync(),
        PanelKind.Statistics => RequestNewStatisticsAsync(),
        PanelKind.ProcessMonitor => RequestNewProcessMonitorAsync(),
        PanelKind.DatabaseViewer => RequestNewDatabaseAsync(),
        PanelKind.Docker => RequestNewDockerAsync(),
        PanelKind.Git => RequestNewGitAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private async Task RequestNewMonitorAsync(PanelKind kind)
    {
        if (kind is not (PanelKind.Statistics or PanelKind.ProcessMonitor))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (!ViewModel.HasRuntimeWorkspace)
        {
            await OpenRuntimeWorkspaceAsync(token =>
                ViewModel.OpenLocalMonitorWorkspaceAsync(kind, token));
            return;
        }

        ViewModel.ShowWorkspace();
        var added = kind == PanelKind.Statistics
            ? await ViewModel.AddStatisticsPanelAsync(_lifetime.Token)
            : await ViewModel.AddProcessMonitorPanelAsync(_lifetime.Token);
        if (added)
        {
            FocusActivePanel();
        }
    }

    public async Task SelectRelativeTabAsync(int offset)
    {
        if (!ViewModel.IsWorkspaceVisible || ViewModel.HasOverlay)
        {
            return;
        }

        if (await ViewModel.SelectRelativeTabAsync(offset, _lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    public async Task RequestClosePanelAsync()
    {
        if (ViewModel.HasOverlay)
        {
            _ = await TryCloseOverlayAsync();
            return;
        }

        if (!ViewModel.IsWorkspaceVisible)
        {
            return;
        }

        await ExecuteCommandAsync(BuiltInCommands.ClosePanel);
    }

    public async Task RequestCloseTabAsync()
    {
        if (ViewModel.HasOverlay)
        {
            _ = await TryCloseOverlayAsync();
            return;
        }

        if (!ViewModel.IsWorkspaceVisible)
        {
            return;
        }

        await ExecuteCommandAsync(BuiltInCommands.CloseTab);
    }

    private async Task OpenDefaultLocalTerminalAsync()
    {
        var connection = ViewModel.Connections.FirstOrDefault(item =>
                item.CanOpen && string.Equals(item.Kind, "Local", StringComparison.OrdinalIgnoreCase))
            ?? ViewModel.Connections.FirstOrDefault(item => item.CanOpen);
        if (connection is null)
        {
            ViewModel.SetError("Create an available connection before opening a terminal.");
            await ShowNewItemLauncherAsync();
            return;
        }

        await OpenRuntimeWorkspaceAsync(token =>
            ViewModel.OpenConnectionAsync(connection.Id, token));
    }

    internal static NewTerminalTarget ResolveNewTerminalTarget(bool hasRuntimeWorkspace) =>
        hasRuntimeWorkspace
            ? NewTerminalTarget.ExistingRuntimeWorkspace
            : NewTerminalTarget.DefaultConnectionWorkspace;

    /// <summary>
    /// Opens something and brings it to the front. It does not close what was
    /// there.
    ///
    /// It used to run the window close flow first — ending every session in the
    /// window — and only then open. That was coherent while the shell held one
    /// runtime workspace, because replacing it did mean ending everything. The
    /// shell holds several now, so clicking a workspace tile was killing the
    /// terminals in every other one, and the "this workspace is already running,
    /// bring it forward" path in OpenWorkspaceAsync could never be reached.
    ///
    /// Closing belongs to closing: the window, or a tab.
    /// </summary>
    private async Task OpenRuntimeWorkspaceAsync(
        Func<CancellationToken, Task<bool>> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        var stall = RuntimeSwitchStall.Begin();
        if (await open(_lifetime.Token))
        {
            ViewModel.CloseOverlay();
            FocusActivePanel();
        }

        stall.ReportWhenDrained(ViewModel.RuntimeWorkspace?.Name);
    }

    /// <summary>
    /// How long the window stays busy after a workspace switch, and where.
    ///
    /// A switch is a chain of things that all happen on the thread that draws:
    /// the view model activates, the dock rebuilds every panel view for the tab
    /// coming forward, and each of those re-attaches to its session. Any of the
    /// three can be the one that costs, and they cannot be told apart from the
    /// outside — the window is simply frozen for all of it.
    ///
    /// So each phase is timed against the dispatcher itself. Work queued behind
    /// layout only runs once layout is done, and work queued behind that only
    /// runs once the frame is out, so the gaps between them are the phases. It
    /// reports only when a switch was slow enough to be felt.
    /// </summary>
    private sealed class RuntimeSwitchStall(Stopwatch clock)
    {
        private readonly Stopwatch _clock = clock;

        /// <summary>
        /// Roughly four frames at sixty hertz. Below this a switch reads as
        /// immediate and there is nothing to say.
        /// </summary>
        private const long BudgetMilliseconds = 64;

        public static RuntimeSwitchStall Begin() => new(Stopwatch.StartNew());

        public void ReportWhenDrained(string? _)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () =>
                        {
                            _clock.Stop();
                            var total = _clock.ElapsedMilliseconds;
                            if (total < BudgetMilliseconds)
                            {
                                return;
                            }

                            SecretSafeDiagnosticProjection.WriteStandardError(
                                "workspace.switch.performance-budget-exceeded",
                                SecretSafeDiagnosticKind.Unexpected);
                        },
                        Avalonia.Threading.DispatcherPriority.Background);
                },
                Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private async void OnActivateTabClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: RuntimeTabViewModel tab })
        {
            try
            {
                ViewModel.ShowWorkspace();
                if (!ViewModel.IsWorkspaceVisible)
                {
                    return;
                }

                if (await ViewModel.ActivateTabAsync(tab.Id, _lifetime.Token))
                {
                    FocusActivePanel();
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private void OnRuntimeTabDragPointerPressed(
        object? sender,
        PointerPressedEventArgs e) =>
        RuntimeTabDrag.PointerPressed(sender, e);

    private void OnRuntimeTabDragPointerMoved(
        object? sender,
        PointerEventArgs e) =>
        RuntimeTabDrag.PointerMoved(sender, e);

    private async void OnRuntimeTabDragPointerReleased(
        object? sender,
        PointerReleasedEventArgs e) =>
        await RuntimeTabDrag.PointerReleasedAsync(sender, e);

    private void OnRuntimeTabDragPointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs e) =>
        RuntimeTabDrag.PointerCaptureLost(sender, e);

    private void OnRuntimeTabDragEnter(object? sender, DragEventArgs e) =>
        RuntimeTabDrag.DragEnter(sender, e);

    private void OnRuntimeTabDragOver(object? sender, DragEventArgs e) =>
        RuntimeTabDrag.DragOver(sender, e);

    private void OnRuntimeTabDragLeave(object? sender, DragEventArgs e) =>
        RuntimeTabDrag.DragLeave(sender, e);

    private async void OnRuntimeTabDrop(object? sender, DragEventArgs e) =>
        await RuntimeTabDrag.DropAsync(sender, e);

    private async void OnRuntimePanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = e;
        await ActivateRuntimePanelAsync(sender);
    }

    private async void OnRuntimePanelGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _ = e;
        await ActivateRuntimePanelAsync(sender);
    }

    private async Task ActivateRuntimePanelAsync(object? sender)
    {
        if (sender is Control { DataContext: RuntimePanelViewModel panel })
        {
            // Selection follows input focus. Do not move focus here: the terminal
            // surface that raised the event must keep keyboard ownership.
            try
            {
                _ = await ViewModel.ActivatePanelAsync(panel.Id, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>
    /// Places an empty panel against an edge of the canvas. It asks what to open
    /// once it is there, so the choice happens in the space the panel will fill.
    /// </summary>
    private async void OnAddPanelToSideRequested(object? sender, PanelSide side)
    {
        _ = sender;
        try
        {
            if (await ViewModel.AddPlaceholderPanelAsync(side, _lifetime.Token))
            {
                FocusActivePanel();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Lifts a panel out of the layout to float over the workspace, or puts it
    /// back where it was.
    ///
    /// One action in two directions, because that is how it reads in the header:
    /// the same corner of the same panel, offering the way out or the way back
    /// depending on where the panel already is.
    /// </summary>
    private void OnFloatRuntimePanelRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: RuntimePanelViewModel panel }
            || ViewModel.RuntimeWorkspace?.ActiveTab is not { } tab)
        {
            return;
        }

        if (tab.IsPanelFloating(panel.Id) ? tab.DockPanel(panel.Id) : tab.FloatPanel(panel.Id))
        {
            ViewModel.MarkVisibleNotificationsSeen();
            FocusActivePanel();
        }
    }

    /// <summary>
    /// Splits a panel, leaving an empty one beside it for the user to fill.
    /// </summary>
    private async void OnSplitRuntimePanelRequested(
        object? sender,
        PanelSplitOrientation orientation)
    {
        if (sender is not Control { DataContext: RuntimePanelViewModel panel })
        {
            return;
        }

        try
        {
            if (await ViewModel.SplitPanelWithPlaceholderAsync(
                panel.Id,
                orientation,
                _lifetime.Token))
            {
                FocusActivePanel();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Turns a placeholder into a real panel. The tab is told which placeholder is
    /// being answered so the created panel takes its cell rather than being
    /// appended wherever the layout would have put it.
    ///
    /// These are the panel-level operations deliberately: the toolbar's "new
    /// terminal" action opens a whole tab, which is right when nothing is being
    /// answered and wrong here — it left the placeholder sitting empty and put the
    /// terminal in a new tab instead of the cell the user had just placed.
    /// </summary>
    private async Task ChoosePlaceholderAsync(object? sender, Func<Task<bool>> create)
    {
        if (sender is not Control source
            || ViewModel.RuntimeWorkspace?.ActiveTab is not { } tab)
        {
            return;
        }

        var placeholder =
            source.DataContext as PanelPlaceholderViewModel
            ?? source.FindAncestorOfType<RuntimePanels.PanelPlaceholderView>()?.DataContext
                as PanelPlaceholderViewModel;
        if (placeholder is null)
        {
            return;
        }

        tab.ReplaceTarget = placeholder.Id;
        try
        {
            await create();
        }
        finally
        {
            tab.ReplaceTarget = null;
        }
    }

    private async void OnPlaceholderTerminalClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddLocalTerminalPanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderBrowserClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddBrowserPanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderStatisticsClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddStatisticsPanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderFileViewerClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddFilePanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderProcessMonitorClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddProcessMonitorPanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderDatabaseClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddDatabasePanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderDockerClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddDockerPanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderGitClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddGitPanelAsync(_lifetime.Token));
    }

    private async void OnDockerShellRequested(
        object? sender,
        DockerRuntimePanelViewModel panel)
    {
        _ = sender;
        if (await ViewModel.OpenDockerContainerShellAsync(panel, _lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnDockerInlineShellRequested(
        object? sender,
        DockerRuntimePanelViewModel panel)
    {
        _ = sender;
        if (await ViewModel.OpenDockerContainerInlineShellAsync(panel, _lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnDockerInlineShellTrustHostKeyRequested(
        object? sender,
        TerminalRuntimePanelViewModel panel)
    {
        _ = sender;
        if (panel.HostKeyReview is not { } review)
        {
            return;
        }

        var confirmed = await new SshHostKeyReviewDialog(review).ShowDialog<bool>(this);
        if (confirmed)
        {
            await panel.TrustHostKeyAsync(_lifetime.Token);
        }
    }

    private void OnDockerInstallGuideRequested(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "https://docs.docker.com/engine/install/",
                UseShellExecute = true,
            });
            if (process is null)
            {
                ViewModel.SetError("The Docker installation guide could not be opened.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            ViewModel.SetError("The Docker installation guide could not be opened.");
        }
    }

    private async void OnPlaceholderConnectionLaunchRequested(
        object? sender,
        SavedConnectionLaunchViewModel launch)
    {
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddSavedConnectionPanelAsync(launch, _lifetime.Token));
    }

    private async void OnCloseRuntimePanelClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: RuntimePanelViewModel panel })
        {
            return;
        }

        if (await CloseRuntimePanelAsync(panel))
        {
            if (await ViewModel.RemovePanelAsync(panel.Id, _lifetime.Token))
            {
                FocusActivePanel();
            }
        }
    }

    private async void OnTerminalConnectionSelected(
        object? sender,
        PanelConnectionSelectedEventArgs e)
    {
        if (e.Selection is not PanelConnectionOptionViewModel.Target.Connection target)
        {
            return;
        }

        if (sender is not Control
            {
                DataContext: TerminalRuntimePanelViewModel panel,
            }
            || panel.ConnectionId == target.Id)
        {
            return;
        }

        await SwitchTerminalConnectionAsync(
            panel,
            () => ViewModel.ReplaceTerminalConnection(panel, target.Id));
    }

    private async void OnTerminalNewConnectionRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control
            {
                DataContext: TerminalRuntimePanelViewModel panel,
            })
        {
            return;
        }

        try
        {
            var editor = ViewModel.CreateUnifiedConnectionEditor(
                SavedConnectionFamily.Terminal);
            var result = await new ConnectionEditorDialog(
                    editor,
                    ConnectionEditorDialogPurpose.Connect)
                .ShowDialog<UnifiedConnectionEditorResult?>(this);
            if (result is not UnifiedConnectionEditorResult.Terminal terminal)
            {
                return;
            }

            if (terminal.SaveConnection)
            {
                var saved = await ViewModel.SaveConnectionAsync(
                    terminal.Request,
                    _lifetime.Token);
                if (!saved.IsSuccess)
                {
                    return;
                }
            }

            await SwitchTerminalConnectionAsync(
                panel,
                () => ViewModel.ReplaceTerminalConnection(panel, terminal.Request.Profile));
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async Task SwitchTerminalConnectionAsync(
        TerminalRuntimePanelViewModel panel,
        Func<bool> replace)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(replace);
        if (!await CloseRuntimePanelAsync(panel))
        {
            return;
        }

        if (replace())
        {
            FocusActivePanel();
        }
    }

    private async void OnPanelConnectionSelected(
        object? sender,
        PanelConnectionSelectedEventArgs e)
    {
        if (sender is not Control { DataContext: RuntimePanelViewModel panel }
            || IsCurrentConnection(panel, e.Selection))
        {
            return;
        }

        if (panel is FileRuntimePanelViewModel files
            && e.Selection is PanelConnectionOptionViewModel.Target.FileProvider fileTarget)
        {
            await SwitchRuntimePanelConnectionAsync(
                panel,
                () => ViewModel.ReplaceFilePanelProfile(files, fileTarget.Id));
            return;
        }

        if (panel is DatabaseRuntimePanelViewModel or RedisRuntimePanelViewModel
            && e.Selection is PanelConnectionOptionViewModel.Target.Database databaseTarget)
        {
            _ = ViewModel.ReplaceDatabasePanelConnection(panel, databaseTarget.Id);
            return;
        }

        if (e.Selection is PanelConnectionOptionViewModel.Target.Connection connectionTarget)
        {
            await SwitchRuntimePanelConnectionAsync(
                panel,
                () => ViewModel.ReplacePanelConnection(panel, connectionTarget.Id));
        }
    }

    private async void OnPanelNewConnectionRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: RuntimePanelViewModel panel })
        {
            return;
        }

        if (panel is FileRuntimePanelViewModel files)
        {
            await CreateAndSwitchFileConnectionAsync(files);
            return;
        }

        if (panel is DatabaseRuntimePanelViewModel or RedisRuntimePanelViewModel)
        {
            await CreateAndBindDatabaseConnectionAsync(panel);
            return;
        }

        try
        {
            var editor = ViewModel.CreateUnifiedConnectionEditor(
                SavedConnectionFamily.Terminal);
            var result = await new ConnectionEditorDialog(
                    editor,
                    ConnectionEditorDialogPurpose.Connect)
                .ShowDialog<UnifiedConnectionEditorResult?>(this);
            if (result is not UnifiedConnectionEditorResult.Terminal terminal)
            {
                return;
            }

            if (terminal.SaveConnection)
            {
                var saved = await ViewModel.SaveConnectionAsync(
                    terminal.Request,
                    _lifetime.Token);
                if (!saved.IsSuccess)
                {
                    return;
                }
            }

            await SwitchRuntimePanelConnectionAsync(
                panel,
                () => ViewModel.ReplacePanelConnection(panel, terminal.Request.Profile));
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    /// <summary>
    /// The database panel's "New connection": the editor opens on the database
    /// family with the connect purpose. Saving binds the persisted profile;
    /// unchecking "Save connection" binds an in-memory one that recovers as a
    /// raw target. Either way the typed password rides along as the session
    /// password so it is not asked for twice.
    /// </summary>
    private async Task CreateAndBindDatabaseConnectionAsync(
        RuntimePanelViewModel panel)
    {
        try
        {
            var editor = ViewModel.CreateUnifiedConnectionEditor(
                SavedConnectionFamily.Database,
                initialFamily: SavedConnectionFamily.Database);
            var result = await new ConnectionEditorDialog(
                    editor,
                    ConnectionEditorDialogPurpose.Connect)
                .ShowDialog<UnifiedConnectionEditorResult?>(this);
            if (result is not UnifiedConnectionEditorResult.Database database)
            {
                return;
            }

            if (database.SaveConnection)
            {
                var profile = await ViewModel.SaveDatabaseConnectionAsync(
                    database.Request.ExistingId,
                    database.Request.Name,
                    database.Request.DriverId,
                    database.Request.Details,
                    database.Request.StorePassword,
                    database.Request.TunnelConnectionId,
                    database.Request.InlineTunnel,
                    _lifetime.Token);
                if (profile is not null)
                {
                    _ = ViewModel.ApplyDatabasePanelConnection(
                        panel,
                        profile,
                        database.Request.Details.Password,
                        ViewModel.ResolveDatabaseTunnel(profile));
                }

                return;
            }

            _ = ViewModel.BindUnsavedDatabaseConnection(panel, database.Request);
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async void OnDatabaseObjectOpenInTabRequested(
        object? sender,
        GhostShell.Application.DatabaseTableDescriptor e)
    {
        if (sender is Control { DataContext: DatabaseRuntimePanelViewModel panel })
        {
            await ViewModel.OpenDatabaseObjectInTabAsync(panel, e, _lifetime.Token);
        }
    }

    private async void OnDatabaseObjectOpenInPanelRequested(
        object? sender,
        GhostShell.Application.DatabaseTableDescriptor e)
    {
        if (sender is Control { DataContext: DatabaseRuntimePanelViewModel panel })
        {
            await ViewModel.OpenDatabaseObjectInPanelAsync(panel, e, _lifetime.Token);
        }
    }

    /// <summary>
    /// The embedded database preview asking to become a real viewer tab, on
    /// the same connection.
    /// </summary>
    private async void OnDatabaseOpenInViewerRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: DatabaseRuntimePanelViewModel panel })
        {
            await ViewModel.OpenDatabaseInTabAsync(panel, _lifetime.Token);
        }
    }

    /// <summary>
    /// The panel's gear: its saved connection opens in the editor, and the
    /// saved changes reconnect the panel through the updated profile.
    /// </summary>
    private async void OnPanelEditDatabaseConnectionRequested(
        object? sender,
        RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: RuntimePanelViewModel panel })
        {
            return;
        }

        var profileId = panel switch
        {
            DatabaseRuntimePanelViewModel relational => relational.SavedConnectionId,
            RedisRuntimePanelViewModel redis => redis.SavedConnectionId,
            _ => null,
        };
        if (profileId is null)
        {
            return;
        }

        try
        {
            var editor = ViewModel.CreateUnifiedConnectionEditor(
                SavedConnectionFamily.Database,
                databaseProfileId: profileId.Value,
                initialFamily: SavedConnectionFamily.Database);
            var result = await new ConnectionEditorDialog(editor)
                .ShowDialog<UnifiedConnectionEditorResult?>(this);
            if (result is not UnifiedConnectionEditorResult.Database database)
            {
                return;
            }

            var profile = await ViewModel.SaveDatabaseConnectionAsync(
                database.Request.ExistingId,
                database.Request.Name,
                database.Request.DriverId,
                database.Request.Details,
                database.Request.StorePassword,
                database.Request.TunnelConnectionId,
                database.Request.InlineTunnel,
                _lifetime.Token);
            if (profile is not null)
            {
                _ = ViewModel.ApplyDatabasePanelConnection(
                    panel,
                    profile,
                    database.Request.Details.Password,
                    ViewModel.ResolveDatabaseTunnel(profile));
            }
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async Task CreateAndSwitchFileConnectionAsync(
        FileRuntimePanelViewModel panel)
    {
        try
        {
            await ViewModel.RefreshSecretsAsync(_lifetime.Token);
            var editor = ViewModel.CreateUnifiedConnectionEditor(
                SavedConnectionFamily.Files,
                initialFamily: SavedConnectionFamily.Files);
            var result = await new ConnectionEditorDialog(
                    editor,
                    ConnectionEditorDialogPurpose.Connect)
                .ShowDialog<UnifiedConnectionEditorResult?>(this);
            if (result is not UnifiedConnectionEditorResult.Files files)
            {
                return;
            }

            var saved = await ViewModel.SaveFileProviderProfileAsync(
                files.Request,
                _lifetime.Token);
            if (!saved.IsSuccess || saved.Value is null)
            {
                return;
            }

            await SwitchRuntimePanelConnectionAsync(
                panel,
                () => ViewModel.ReplaceFilePanelProfile(
                    panel,
                    saved.Value.Value.Id));
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async Task SwitchRuntimePanelConnectionAsync(
        RuntimePanelViewModel panel,
        Func<bool> replace)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(replace);
        if (!await CloseRuntimePanelAsync(panel))
        {
            return;
        }

        if (replace())
        {
            FocusActivePanel();
        }
    }

    private static bool IsCurrentConnection(
        RuntimePanelViewModel panel,
        PanelConnectionOptionViewModel.Target selection) =>
        (panel, selection) switch
        {
            (
                BrowserRuntimePanelViewModel browser,
                PanelConnectionOptionViewModel.Target.Connection target) =>
                browser.ConnectionId == target.Id,
            (
                FileRuntimePanelViewModel files,
                PanelConnectionOptionViewModel.Target.FileProvider target) =>
                files.UsesProfile(target.Id),
            (
                StatisticsRuntimePanelViewModel statistics,
                PanelConnectionOptionViewModel.Target.Connection target) =>
                statistics.ConnectionId == target.Id,
            (
                ProcessMonitorRuntimePanelViewModel processes,
                PanelConnectionOptionViewModel.Target.Connection target) =>
                processes.ConnectionId == target.Id,
            (
                DockerRuntimePanelViewModel docker,
                PanelConnectionOptionViewModel.Target.Connection target) =>
                docker.ConnectionId == target.Id,
            (
                DatabaseRuntimePanelViewModel database,
                PanelConnectionOptionViewModel.Target.Database target) =>
                database.SavedConnectionId == target.Id,
            (
                RedisRuntimePanelViewModel redis,
                PanelConnectionOptionViewModel.Target.Database target) =>
                redis.SavedConnectionId == target.Id,
            _ => false,
        };

    private async void OnRetryConnectionPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: TerminalRuntimePanelViewModel panel }
            && panel.CanRetry)
        {
            await panel.RetryAsync();
        }
    }

    private void OnCancelConnectionReconnectClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: TerminalRuntimePanelViewModel panel })
        {
            panel.CancelConnection();
        }
    }

    private async void OnTrustConnectionHostKeyClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: TerminalRuntimePanelViewModel panel }
            || panel.HostKeyReview is not { } review)
        {
            return;
        }

        var confirmed = await new SshHostKeyReviewDialog(review).ShowDialog<bool>(this);
        if (confirmed)
        {
            await panel.TrustHostKeyAsync(_lifetime.Token);
        }
    }

    private void OnTerminalSessionSnapshotChanged(
        object? sender,
        TerminalSessionSnapshotEventArgs e)
    {
        if (sender is Control { DataContext: TerminalRuntimePanelViewModel panel })
        {
            panel.ObserveSessionSnapshot(e.Snapshot);
        }
    }

    private void OnTerminalSessionInitializationFailed(
        object? sender,
        TerminalSessionFailureEventArgs e)
    {
        if (sender is Control { DataContext: TerminalRuntimePanelViewModel panel })
        {
            panel.ObserveSessionInitializationFailure(e.Failure);
        }
    }

    private void OnBrowserStateChanged(
        object? sender,
        BrowserStateChangedEventArgs e)
    {
        if (sender is BrowserPresentationHost
            {
                DataContext: BrowserRuntimePanelViewModel panel,
            })
        {
            panel.ApplyBrowserState(e.State);
        }
    }

    private async void OnBrowserAddressKeyDown(object? sender, KeyEventArgs e)
    {
        // The view raises this with its presentation host as the sender, and the
        // address box writes straight to the host, so the typed text is already
        // there to navigate to.
        if (e.Key != Key.Enter || sender is not BrowserPresentationHost browser)
        {
            return;
        }

        e.Handled = true;
        await RunBrowserOperationAsync(token =>
            browser.NavigateAddressAsync(browser.AddressText, token));
    }

    private async void OnBrowserBackClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is BrowserPresentationHost browser)
        {
            await RunBrowserOperationAsync(browser.GoBackAsync);
        }
    }

    private async void OnBrowserForwardClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is BrowserPresentationHost browser)
        {
            await RunBrowserOperationAsync(browser.GoForwardAsync);
        }
    }

    private void OnBrowserDeveloperToolsClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is BrowserPresentationHost browser)
        {
            browser.OpenDeveloperTools();
        }
    }

    private void OnBrowserOpenInSystemBrowserClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is BrowserPresentationHost browser)
        {
            browser.OpenInSystemBrowser();
        }
    }

    private async void OnBrowserReloadClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is BrowserPresentationHost browser)
        {
            await RunBrowserOperationAsync(browser.ReloadAsync);
        }
    }

    private async void OnBrowserStopClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is BrowserPresentationHost browser)
        {
            await RunBrowserOperationAsync(browser.StopAsync);
        }
    }

    private async Task RunBrowserOperationAsync(
        Func<CancellationToken, ValueTask> operation)
    {
        try
        {
            await operation(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    /// <summary>
    /// Every file action arrives here, from whichever of the three places it was
    /// shown in. What each one takes to carry out — a folder picker, a name
    /// typed into a dialog, a confirmation — is the window's to provide, which
    /// is why the panel asks rather than acts.
    /// </summary>
    private async void OnFileActionRequested(object? sender, FilePanelActionEventArgs e)
    {
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel })
        {
            return;
        }

        switch (e.Action)
        {
            case FilePanelAction.Open:
                if (panel.SelectedEntry is { } opened)
                {
                    await panel.OpenEntryAsync(opened, _lifetime.Token);
                }

                return;
            case FilePanelAction.Copy:
            case FilePanelAction.Cut:
                HoldOnTransferClipboard(
                    panel,
                    panel.SelectedEntriesOrCurrent,
                    cut: e.Action == FilePanelAction.Cut);
                return;
            case FilePanelAction.Paste:
                await PasteFromTransferClipboardAsync(panel);
                return;
            case FilePanelAction.CopyName:
                await CopyFileTextAsync(panel.SelectedEntry?.Name);
                return;
            case FilePanelAction.CopyPath:
                await CopyFileTextAsync(panel.SelectedEntryPath);
                return;
            case FilePanelAction.AccessControl:
                await ShowFileAccessControlAsync(panel);
                return;
            case FilePanelAction.Refresh:
                await panel.RefreshAsync(_lifetime.Token);
                return;
        }

        switch (e.Action)
        {
            case FilePanelAction.OpenExternally:
                OnFileOpenExternallyClick(sender, e);
                break;
            case FilePanelAction.Download:
                OnFileDownloadClick(sender, e);
                break;
            case FilePanelAction.Upload:
                OnFileUploadClick(sender, e);
                break;
            case FilePanelAction.Transfer:
                OnFileTransferClick(sender, e);
                break;
            case FilePanelAction.NewFolder:
                OnFileCreateFolderClick(sender, e);
                break;
            case FilePanelAction.Rename:
                OnFileRenameClick(sender, e);
                break;
            case FilePanelAction.Delete:
                OnFileDeleteClick(sender, e);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(e), e.Action, null);
        }
    }

    private async void OnFileNavigateUpClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.NavigateUpAsync(_lifetime.Token);
        }
    }

    private async void OnFileRefreshClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.RefreshAsync(_lifetime.Token);
        }
    }

    private async void OnFileLocationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.NavigateFromTextAsync(_lifetime.Token);
            e.Handled = true;
        }
    }

    private async void OnFileEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        _ = e;
        if (sender is ListBox
            {
                DataContext: FileRuntimePanelViewModel panel,
                SelectedItem: FileEntryViewModel entry,
            })
        {
            await panel.OpenEntryAsync(entry, _lifetime.Token);
        }
    }

    private async void OnFileEntrySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        if (sender is ListBox { DataContext: FileRuntimePanelViewModel panel } list)
        {
            // The whole selection, not just the item whose preview is shown:
            // downloading and transferring act on everything picked.
            panel.SetSelectedEntries(list.SelectedItems?
                .OfType<FileEntryViewModel>()
                .Select(item => item.Entry)
                .ToArray() ?? []);
            await panel.PreviewSelectedAsync(_lifetime.Token);
        }
    }

    private async void OnFileEntryTransferKeyRequested(
        object? sender,
        FilePanelTransferKeyEventArgs e)
    {
        if (sender is not ListBox { DataContext: FileRuntimePanelViewModel panel })
        {
            return;
        }

        if (e.KeyEvent.Key is Key.C or Key.X)
        {
            if (e.Entries.Count == 0)
            {
                return;
            }

            HoldOnTransferClipboard(
                panel,
                e.Entries,
                cut: e.KeyEvent.Key == Key.X);
            e.KeyEvent.Handled = true;
            return;
        }

        if (e.KeyEvent.Key != Key.V)
        {
            return;
        }

        e.KeyEvent.Handled = true;
        await PasteFromTransferClipboardAsync(panel);
    }

    /// <summary>
    /// Copy and cut differ only in what becomes of the original, and that is
    /// decided when it lands rather than now: nothing is taken from a folder
    /// until the transfer into the other one has been queued.
    /// </summary>
    private void HoldOnTransferClipboard(
        FileRuntimePanelViewModel panel,
        IReadOnlyList<FilePanelEntry> entries,
        bool cut)
    {
        if (entries.Count == 0)
        {
            return;
        }

        ViewModel.FileTransferClipboard.Payload = new FilePanelTransferPayload(
            panel.Id,
            entries,
            cut ? FilePanelTransferOperation.Move : FilePanelTransferOperation.Copy);
    }

    /// <summary>
    /// Who can read or change the selected item. Read first, because there is
    /// nothing to show until the connection has answered, and because what it
    /// answers decides which of the two editors the dialog puts up.
    /// </summary>
    private async Task ShowFileAccessControlAsync(FileRuntimePanelViewModel panel)
    {
        if (panel.SelectedEntry is not { } selected
            || await panel.ReadAccessControlAsync(_lifetime.Token) is not { } accessControl)
        {
            return;
        }

        var editor = new FileAccessControlEditorViewModel(
            selected.Name,
            panel.SelectedProfile?.Name ?? "this connection",
            accessControl,
            // A connection that describes access does not necessarily accept a
            // change to it, and saying so before an Apply is kinder than after.
            canEdit: panel.SelectedProfile?.Capabilities.HasFlag(
                FilePanelCapability.Permissions) == true
                || panel.SelectedProfile?.Capabilities.HasFlag(
                    FilePanelCapability.AccessControlLists) == true);
        var request = await new FileAccessControlDialog(editor)
            .ShowDialog<FilePanelSetAccessControlRequest?>(this);
        if (request is not null)
        {
            _ = await panel.WriteAccessControlAsync(request, _lifetime.Token);
        }
    }

    /// <summary>
    /// The system clipboard, which is the window's — unlike the transfer
    /// clipboard, a name or a path means something outside this shell.
    /// </summary>
    private async Task CopyFileTextAsync(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            await ClipboardWriter.WriteTextAsync(text);
        }
    }

    private async Task PasteFromTransferClipboardAsync(FileRuntimePanelViewModel panel)
    {
        if (ViewModel.FileTransferClipboard.Payload is not { } clipboard)
        {
            panel.ReportValidationError("Copy or cut a file or folder first.");
            return;
        }

        if (await QueueIncomingFileTransferAsync(panel, clipboard)
            && clipboard.Operation == FilePanelTransferOperation.Move)
        {
            ViewModel.FileTransferClipboard.Payload = null;
        }
    }

    private async void OnFileEntryTransferDropRequested(
        object? sender,
        FilePanelTransferDropEventArgs e)
    {
        _ = sender;
        _ = await QueueIncomingFileTransferAsync(
            e.Destination,
            e.Payload,
            e.DestinationFolder);
    }

    private async Task<bool> QueueIncomingFileTransferAsync(
        FileRuntimePanelViewModel destination,
        FilePanelTransferPayload payload,
        FilePanelLocation? destinationFolder = null)
    {
        var queuedAll = true;
        foreach (var entry in payload.Entries)
        {
            try
            {
                var request = destination.CreateIncomingTransferRequest(
                    entry,
                    payload.Operation,
                    destinationFolder);
                // Queue through the destination panel's owner-scoped client so
                // completion can be routed back to this exact panel. The shell
                // still observes the shared projection for its transfer list.
                queuedAll &= await destination.QueueTransferAsync(
                    request,
                    _lifetime.Token);
            }
            catch (ArgumentException exception)
            {
                destination.ReportValidationError(exception.Message);
                queuedAll = false;
            }
            catch (InvalidOperationException exception)
            {
                destination.ReportValidationError(exception.Message);
                queuedAll = false;
            }
        }

        return queuedAll;
    }

    private async void OnFileCreateFolderClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel })
        {
            return;
        }

        var name = await new FileNameDialog("New folder", "Create")
            .ShowDialog<string?>(this);
        if (name is not null)
        {
            _ = await panel.CreateFolderAsync(name, _lifetime.Token);
        }
    }

    private async void OnFileTransferClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel }
            || !panel.CanTransfer)
        {
            return;
        }

        var request = await new FileTransferDialog(panel.CreateTransferEditor())
            .ShowDialog<FilePanelTransferRequest?>(this);
        if (request is not null)
        {
            _ = await panel.QueueTransferAsync(request, _lifetime.Token);
        }
    }

    private async void OnFileDownloadClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel }
            || !panel.CanDownload)
        {
            return;
        }

        // A folder on this machine, chosen the way the system chooses folders,
        // rather than a path typed into a dialog.
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Download to folder",
            AllowMultiple = false,
        });
        if (folders.Count != 1 || folders[0].TryGetLocalPath() is not { } destination)
        {
            return;
        }

        foreach (var request in panel.CreateDownloadRequests(destination))
        {
            _ = await panel.QueueTransferAsync(request, _lifetime.Token);
        }
    }

    private async void OnFileUploadClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel }
            || !panel.CanUpload)
        {
            return;
        }

        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Upload a file from Home",
            AllowMultiple = false,
        });
        var path = selected.Count == 1 ? selected[0].TryGetLocalPath() : null;
        if (path is null)
        {
            return;
        }

        try
        {
            var request = await new FileTransferDialog(panel.CreateUploadEditor(path))
                .ShowDialog<FilePanelTransferRequest?>(this);
            if (request is not null)
            {
                _ = await panel.QueueTransferAsync(request, _lifetime.Token);
            }
        }
        catch (ArgumentException exception)
        {
            panel.ReportValidationError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            panel.ReportValidationError(exception.Message);
        }
    }

    private void OnFileOpenExternallyClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel }
            || !panel.CanOpenExternally)
        {
            return;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = panel.GetSelectedLocalPath(),
                UseShellExecute = true,
            });
            if (process is null)
            {
                panel.ReportValidationError(
                    "The operating system did not provide an application for this file.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            panel.ReportValidationError(
                "The operating system could not open this file with its default application.");
        }
    }

    private async void OnFileRenameClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control
            {
                DataContext: FileRuntimePanelViewModel
                {
                    SelectedEntry: { } selected,
                } panel,
            })
        {
            return;
        }

        var name = await new FileNameDialog("Rename item", "Rename", selected.Name)
            .ShowDialog<string?>(this);
        if (name is not null)
        {
            _ = await panel.RenameSelectedAsync(name, _lifetime.Token);
        }
    }

    private async void OnFileDeleteClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control
            {
                DataContext: FileRuntimePanelViewModel
                {
                    SelectedEntry: { } selected,
                } panel,
            })
        {
            return;
        }

        var kind = selected.IsDirectory ? "folder" : "file";
        var confirmed = await Confirmations.FileDelete(
                kind,
                selected.Name,
                panel.SelectedProfile?.Name ?? "this provider",
                panel.SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Versioning) == true)
            .ShowDialog<bool>(this);
        if (confirmed)
        {
            _ = await panel.DeleteSelectedAsync(_lifetime.Token);
        }
    }

    private void OnDismissFileOperationIssueClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            panel.ClearOperationIssue();
        }
    }

    private async void OnCloseRuntimeTabClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: RuntimeTabViewModel tab })
        {
            return;
        }

        if (!await ConfirmDiscardDatabaseChangesAsync(tab.Panels))
        {
            return;
        }

        if (tab.Panels.All(panel => panel is FileRuntimePanelViewModel { HostedClient: null }
                or UnavailableRuntimePanelViewModel
                or TerminalRuntimePanelViewModel
            { SessionRequest: null, MultiplexerSession: null }
                or StatisticsRuntimePanelViewModel { HasHostedSession: false }
                or ProcessMonitorRuntimePanelViewModel { HasHostedSession: false })
            || await RunCloseFlowAsync(
            (decision, cancellationToken) =>
                ViewModel.CloseTabAsync(tab.Id, decision, cancellationToken)))
        {
            if (await ViewModel.RemoveTabAsync(tab.Id, _lifetime.Token))
            {
                FocusActivePanel();
            }
        }
    }

    private async void OnAddTerminalPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddLocalTerminalPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddConnectionPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: LauncherConnectionViewModel connection })
        {
            return;
        }

        if (await ViewModel.AddConnectionPanelAsync(connection.Id, _lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddFilePanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddFilePanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddBrowserPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var profileId = ViewModel.SelectedBrowserPanelProfile?.Id;
        if (await ViewModel.AddBrowserPanelAsync(profileId, _lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddStatisticsPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddStatisticsPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddProcessMonitorPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddProcessMonitorPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddDatabasePanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddDatabasePanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddDockerPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddDockerPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddGitPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddGitPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async Task CloseActivePanelAsync()
    {
        if (ViewModel.ActivePanel is not { } panel)
        {
            return;
        }

        if (await CloseRuntimePanelAsync(panel))
        {
            _ = await ViewModel.RemovePanelAsync(panel.Id, _lifetime.Token);
        }
    }

    private async Task<bool> CloseRuntimePanelAsync(RuntimePanelViewModel panel)
    {
        if (!await ConfirmDiscardDatabaseChangesAsync([panel]))
        {
            return false;
        }

        return await (panel switch
        {
            UnavailableRuntimePanelViewModel => Task.FromResult(true),
            FileRuntimePanelViewModel { HostedClient: null } => Task.FromResult(true),
            TerminalRuntimePanelViewModel
            { SessionRequest: null, MultiplexerSession: null } => Task.FromResult(true),
            TerminalRuntimePanelViewModel
            { SessionRequest: null, MultiplexerSession: not null } terminal =>
                CloseDetachedMultiplexedTerminalAsync(terminal),
            StatisticsRuntimePanelViewModel { HasHostedSession: false } => Task.FromResult(true),
            ProcessMonitorRuntimePanelViewModel { HasHostedSession: false } => Task.FromResult(true),
            FileRuntimePanelViewModel filePanel => RunCloseFlowAsync((decision, token) =>
                ViewModel.CloseFilePanelAsync(filePanel, decision, token)),
            _ => RunCloseFlowAsync((decision, token) =>
                ViewModel.ClosePanelAsync(panel.Id, decision, token)),
        });
    }

    private async Task<bool> CloseDetachedMultiplexedTerminalAsync(
        TerminalRuntimePanelViewModel terminal)
    {
        await ViewModel.CloseDetachedMultiplexedTerminalAsync(terminal, _lifetime.Token);
        return true;
    }

    private async Task CloseActiveTabAsync()
    {
        if (ViewModel.RuntimeWorkspace?.ActiveTab is { } tab
            && await ConfirmDiscardDatabaseChangesAsync(tab.Panels)
            && await RunCloseFlowAsync((decision, token) =>
                ViewModel.CloseTabAsync(tab.Id, decision, token)))
        {
            _ = await ViewModel.RemoveTabAsync(tab.Id, _lifetime.Token);
        }
    }

    private async Task<bool> ConfirmDiscardDatabaseChangesAsync(
        IEnumerable<RuntimePanelViewModel> panels) =>
        await CloseCoordinator.ConfirmDiscardDatabaseChangesAsync(panels);

    private async Task RenameActiveTabAsync()
    {
        if (ViewModel.RuntimeWorkspace?.ActiveTab is not { } tab)
        {
            return;
        }

        var title = await new FileNameDialog(
            "Rename tab",
            "Rename",
            tab.Title,
            "This name identifies the live tab; saved screen definitions are unchanged.")
            .ShowDialog<string?>(this);
        if (title is not null)
        {
            _ = await ViewModel.RenameActiveTabAsync(title, _lifetime.Token);
        }
    }

    private async void OnRuntimeTabTitleEditRequested(
        object? sender,
        RuntimeTabTitleEditRequestedEventArgs e)
    {
        _ = sender;
        if (e.Tab is not RuntimeTabViewModel tab)
        {
            return;
        }

        _ = await ViewModel.UpdateRuntimeTabIdentityAsync(
            tab.Id,
            e.Title,
            tab.Icon,
            _lifetime.Token);
    }

    private void OnRuntimeTabIconEditRequested(
        object? sender,
        RuntimeTabIconEditRequestedEventArgs e)
    {
        _ = sender;
        if (e.Tab is not RuntimeTabViewModel tab)
        {
            return;
        }

        _ = ViewModel.ChooseRuntimeTabIcon(tab.Id, e.Icon);
    }

    private void FocusActivePanel() => FocusNavigator.FocusActivePanel();

    private TerminalPresentationHost? FindActiveTerminalHost() =>
        FocusNavigator.FindActiveTerminalHost();
}
