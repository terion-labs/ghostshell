using System.Diagnostics;
using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Views;

public sealed partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
        // One handler for every panel on the canvas, floating or docked. Floating
        // used to mean a second window, and an event cannot bubble across two of
        // them; now that a floated panel stays in this one, the whole canvas can
        // be answered in one place instead of eight views forwarding the same
        // request.
        AddHandler(PanelChrome.FloatToggleRequestedEvent, OnFloatToggleRequested);
    }

    /// <summary>
    /// Whether a panel floats over the workspace is the shell's to decide, so
    /// this only carries the request up. The view's part is knowing which panel
    /// asked — which is why the event is caught here rather than eight panel
    /// views each forwarding one of their own.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? FloatPanelRequested;

    private void OnFloatToggleRequested(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        FloatPanelRequested?.Invoke(e.Source, e);
    }

    public event EventHandler<RoutedEventArgs>? ActivateTabRequested;

    public event EventHandler<RoutedEventArgs>? ApproveAgentActionRequested;

    public event EventHandler<KeyEventArgs>? AgentQuestionResponseKeyDownRequested;

    public event EventHandler<RoutedEventArgs>? CancelAgentActionRequested;

    public event EventHandler<RoutedEventArgs>? CancelAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? CancelFileTransferRequested;

    public event EventHandler<RoutedEventArgs>? ClearAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? ClearAgentSavedScreenTargetRequested;

    public event EventHandler<RoutedEventArgs>? CreateAgentSavedScreenTargetRequested;

    public event EventHandler<RoutedEventArgs>? CloseRuntimeTabRequested;

    public event EventHandler<RuntimeTabTitleEditRequestedEventArgs>?
        RuntimeTabTitleEditRequested;

    public event EventHandler<RuntimeTabIconEditRequestedEventArgs>?
        RuntimeTabIconEditRequested;

    public event EventHandler<RoutedEventArgs>? DeclineAgentQuestionRequested;

    public event EventHandler<RoutedEventArgs>? DenyAgentActionRequested;

    public event EventHandler<RoutedEventArgs>? DisableAgentYoloRequested;

    public event EventHandler<RoutedEventArgs>? EnableAgentCapabilityAskRequested;

    public event EventHandler<RoutedEventArgs>? EnableAgentYoloRequested;

    public event EventHandler<RoutedEventArgs>? KeepAgentCapabilityOffRequested;

    public event EventHandler<RoutedEventArgs>? LoadOlderAgentAuditRequested;

    public event EventHandler<AgentQueuedFollowUpMoveRequestedEventArgs>?
        MoveQueuedFollowUpRequested;

    public event EventHandler<RoutedEventArgs>? OpenWorkspaceRequested;

    public event EventHandler<RoutedEventArgs>? CloseWorkspaceRequested;
    public event EventHandler<RoutedEventArgs>? SaveWorkspaceLayoutRequested;

    private void OnSaveWorkspaceLayoutClick(object? sender, RoutedEventArgs e) =>
        SaveWorkspaceLayoutRequested?.Invoke(sender, e);

    /// <summary>
    /// The rail lists workspaces, so the button under them makes another one.
    /// It used to add a tab, which is the one thing the rail has nothing to do
    /// with.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? CreateWorkspaceRequested;

    public event EventHandler<RoutedEventArgs>? RefreshAgentAuditRequested;

    public event EventHandler<RoutedEventArgs>? ApplyAgentHistoryRetentionRequested;

    public event EventHandler<RoutedEventArgs>? AuthorizeAgentSavedScreenTargetRequested;

    public event EventHandler<RoutedEventArgs>? ExportAgentHistoryRequested;

    public event EventHandler<RoutedEventArgs>? StartNewAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? OpenAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? DeleteAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? CopyAgentMessageRequested;

    public event EventHandler<RoutedEventArgs>? ForkAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? SelectAgentModelRequested;

    public event EventHandler<RoutedEventArgs>? ToggleAgentModelFavoriteRequested;

    public event EventHandler<RoutedEventArgs>? RefreshAgentModelsRequested;

    public event EventHandler<RoutedEventArgs>? RetryFileTransferRequested;

    public event EventHandler<DragEventArgs>? RuntimeTabDragEnterRequested;

    public event EventHandler<DragEventArgs>? RuntimeTabDragLeaveRequested;

    public event EventHandler<DragEventArgs>? RuntimeTabDragOverRequested;

    public event EventHandler<PointerCaptureLostEventArgs>?
        RuntimeTabDragPointerCaptureLostRequested;

    public event EventHandler<PointerEventArgs>? RuntimeTabDragPointerMovedRequested;

    public event EventHandler<PointerPressedEventArgs>? RuntimeTabDragPointerPressedRequested;

    public event EventHandler<PointerReleasedEventArgs>? RuntimeTabDragPointerReleasedRequested;

    public event EventHandler<DragEventArgs>? RuntimeTabDropRequested;

    public event EventHandler<RoutedEventArgs>? SendAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? QueueAgentSteeringRequested;

    public event EventHandler<RoutedEventArgs>? AttachAgentImageRequested;

    public event EventHandler<RoutedEventArgs>? ClearAgentImagesRequested;

    public event EventHandler<RoutedEventArgs>? ShowAgentSettingsRequested;

    public event EventHandler<RoutedEventArgs>? ShowCommandPaletteRequested;

    public event EventHandler<RoutedEventArgs>? ShowLauncherRequested;

    public event EventHandler<RoutedEventArgs>? ShowNewItemRequested;

    public event EventHandler<RoutedEventArgs>? ShowNewPanelRequested;

    /// <summary>
    /// Which edge the user chose for a new panel. The view forwards the side; the
    /// shell places an empty panel there and lets it ask what to open.
    /// </summary>
    public event EventHandler<PanelSide>? AddPanelToSideRequested;

    public event EventHandler<RoutedEventArgs>? LockShellRequested;

    public event EventHandler<RoutedEventArgs>? ShowSettingsRequested;

    public event EventHandler<RoutedEventArgs>? SubmitAgentQuestionRequested;

    public event EventHandler<RoutedEventArgs>? ToggleAgentRequested;

    public event EventHandler<RoutedEventArgs>? ToggleAgentPinRequested;

    public event EventHandler<WorkspaceNetworkConnectionOptionViewModel>?
        WorkspaceNetworkConnectionSelected;

    public event EventHandler<RoutedEventArgs>? WorkspaceNetworkToggleRequested;

    /// <summary>The network flyout asked for the page where connections are made.</summary>
    public event EventHandler<RoutedEventArgs>? NetworkingSettingsRequested;

    private void OnActivateTabClick(object? sender, RoutedEventArgs e) =>
        ActivateTabRequested?.Invoke(sender, e);

    private void OnWorkspaceNetworkConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control
            {
                DataContext: WorkspaceNetworkConnectionOptionViewModel connection,
            })
        {
            WorkspaceNetworkConnectionSelected?.Invoke(this, connection);
        }
    }

    private void OnWorkspaceNetworkToggleClick(object? sender, RoutedEventArgs e) =>
        WorkspaceNetworkToggleRequested?.Invoke(sender, e);

    private void OnNetworkingSettingsClick(object? sender, RoutedEventArgs e)
    {
        NetworkingSettingsRequested?.Invoke(sender, e);
        // Deferred: hiding a flyout inside its own button's Click detaches the
        // content mid-dispatch, and the settings route has replaced this view
        // by the time the popup would otherwise notice.
        Dispatcher.UIThread.Post(
            () => this.FindControl<Button>("WorkspaceNetworkButton")?.Flyout?.Hide(),
            DispatcherPriority.Background);
    }

    private void OnAgentQuestionResponseKeyDown(object? sender, KeyEventArgs e) =>
        AgentQuestionResponseKeyDownRequested?.Invoke(sender, e);

    private void OnApproveAgentActionClick(object? sender, RoutedEventArgs e) =>
        ApproveAgentActionRequested?.Invoke(sender, e);

    private void OnCancelAgentActionClick(object? sender, RoutedEventArgs e) =>
        CancelAgentActionRequested?.Invoke(sender, e);

    private void OnCancelAgentChatClick(object? sender, RoutedEventArgs e) =>
        CancelAgentChatRequested?.Invoke(sender, e);

    private void OnCancelFileTransferClick(object? sender, RoutedEventArgs e) =>
        CancelFileTransferRequested?.Invoke(sender, e);

    private void OnClearAgentChatClick(object? sender, RoutedEventArgs e) =>
        ClearAgentChatRequested?.Invoke(sender, e);

    private void OnCloseRuntimeTabClick(object? sender, RoutedEventArgs e) =>
        CloseRuntimeTabRequested?.Invoke(sender, e);

    private void OnRuntimeTabTitleEditRequested(
        object? sender,
        RuntimeTabTitleEditRequestedEventArgs e) =>
        RuntimeTabTitleEditRequested?.Invoke(sender, e);

    private void OnRuntimeTabIconEditRequested(
        object? sender,
        RuntimeTabIconEditRequestedEventArgs e) =>
        RuntimeTabIconEditRequested?.Invoke(sender, e);

    private void OnDeclineAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        DeclineAgentQuestionRequested?.Invoke(sender, e);

    private void OnDenyAgentActionClick(object? sender, RoutedEventArgs e) =>
        DenyAgentActionRequested?.Invoke(sender, e);

    private void OnDisableAgentYoloClick(object? sender, RoutedEventArgs e) =>
        DisableAgentYoloRequested?.Invoke(sender, e);

    private void OnEnableAgentCapabilityAskClick(object? sender, RoutedEventArgs e) =>
        EnableAgentCapabilityAskRequested?.Invoke(sender, e);

    private void OnEnableAgentYoloClick(object? sender, RoutedEventArgs e) =>
        EnableAgentYoloRequested?.Invoke(sender, e);

    private void OnKeepAgentCapabilityOffClick(object? sender, RoutedEventArgs e) =>
        KeepAgentCapabilityOffRequested?.Invoke(sender, e);

    private void OnLoadOlderAgentAuditClick(object? sender, RoutedEventArgs e) =>
        LoadOlderAgentAuditRequested?.Invoke(sender, e);

    private void OnMoveAgentQueuedFollowUpRequested(
        object? sender,
        AgentQueuedFollowUpMoveRequestedEventArgs e) =>
        MoveQueuedFollowUpRequested?.Invoke(sender, e);

    private void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e) =>
        OpenWorkspaceRequested?.Invoke(sender, e);

    private void OnCloseWorkspaceClick(object? sender, RoutedEventArgs e) =>
        CloseWorkspaceRequested?.Invoke(sender, e);

    private void OnCreateWorkspaceClick(object? sender, RoutedEventArgs e) =>
        CreateWorkspaceRequested?.Invoke(sender, e);

    private void OnRefreshAgentAuditClick(object? sender, RoutedEventArgs e) =>
        RefreshAgentAuditRequested?.Invoke(sender, e);

    private void OnApplyAgentHistoryRetentionClick(object? sender, RoutedEventArgs e) =>
        ApplyAgentHistoryRetentionRequested?.Invoke(sender, e);

    private void OnExportAgentHistoryClick(object? sender, RoutedEventArgs e) =>
        ExportAgentHistoryRequested?.Invoke(sender, e);

    private void OnStartNewAgentConversationClick(object? sender, RoutedEventArgs e) =>
        StartNewAgentConversationRequested?.Invoke(sender, e);

    private void OnOpenAgentConversationClick(object? sender, RoutedEventArgs e) =>
        OpenAgentConversationRequested?.Invoke(sender, e);

    private void OnDeleteAgentConversationClick(object? sender, RoutedEventArgs e) =>
        DeleteAgentConversationRequested?.Invoke(sender, e);

    private void OnCopyAgentMessageClick(object? sender, RoutedEventArgs e) =>
        CopyAgentMessageRequested?.Invoke(sender, e);

    private void OnForkAgentConversationClick(object? sender, RoutedEventArgs e) =>
        ForkAgentConversationRequested?.Invoke(sender, e);

    private void OnSelectAgentModelClick(object? sender, RoutedEventArgs e) =>
        SelectAgentModelRequested?.Invoke(sender, e);

    private void OnToggleAgentModelFavoriteClick(object? sender, RoutedEventArgs e) =>
        ToggleAgentModelFavoriteRequested?.Invoke(sender, e);

    private void OnRefreshAgentModelsClick(object? sender, RoutedEventArgs e) =>
        RefreshAgentModelsRequested?.Invoke(sender, e);

    private void OnRetryFileTransferClick(object? sender, RoutedEventArgs e) =>
        RetryFileTransferRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragEnter(object? sender, DragEventArgs e) =>
        RuntimeTabDragEnterRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragLeave(object? sender, DragEventArgs e) =>
        RuntimeTabDragLeaveRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragOver(object? sender, DragEventArgs e) =>
        RuntimeTabDragOverRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragPointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs e) =>
        RuntimeTabDragPointerCaptureLostRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragPointerMoved(object? sender, PointerEventArgs e) =>
        RuntimeTabDragPointerMovedRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragPointerPressed(
        object? sender,
        PointerPressedEventArgs e) =>
        RuntimeTabDragPointerPressedRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragPointerReleased(
        object? sender,
        PointerReleasedEventArgs e) =>
        RuntimeTabDragPointerReleasedRequested?.Invoke(sender, e);

    private void OnRuntimeTabDrop(object? sender, DragEventArgs e) =>
        RuntimeTabDropRequested?.Invoke(sender, e);

    private void OnSendAgentChatClick(object? sender, RoutedEventArgs e) =>
        SendAgentChatRequested?.Invoke(sender, e);

    private void OnClearAgentSavedScreenTargetClick(object? sender, RoutedEventArgs e) =>
        ClearAgentSavedScreenTargetRequested?.Invoke(sender, e);

    private void OnCreateAgentSavedScreenTargetClick(object? sender, RoutedEventArgs e) =>
        CreateAgentSavedScreenTargetRequested?.Invoke(sender, e);

    private void OnAuthorizeAgentSavedScreenTargetClick(object? sender, RoutedEventArgs e) =>
        AuthorizeAgentSavedScreenTargetRequested?.Invoke(sender, e);

    private void OnQueueAgentSteeringClick(object? sender, RoutedEventArgs e) =>
        QueueAgentSteeringRequested?.Invoke(sender, e);

    private void OnAttachAgentImageClick(object? sender, RoutedEventArgs e) =>
        AttachAgentImageRequested?.Invoke(sender, e);

    private void OnClearAgentImagesClick(object? sender, RoutedEventArgs e) =>
        ClearAgentImagesRequested?.Invoke(sender, e);

    private void OnShowAgentSettingsClick(object? sender, RoutedEventArgs e) =>
        ShowAgentSettingsRequested?.Invoke(sender, e);

    private void OnShowCommandPaletteClick(object? sender, RoutedEventArgs e) =>
        ShowCommandPaletteRequested?.Invoke(sender, e);

    private void OnShowLauncherClick(object? sender, RoutedEventArgs e) =>
        ShowLauncherRequested?.Invoke(sender, e);

    private void OnShowNewItemClick(object? sender, RoutedEventArgs e) =>
        ShowNewItemRequested?.Invoke(sender, e);

    /// <summary>
    /// The shell reads which workspace was chosen from the sender's data
    /// context, and hiding the menu first takes that context away with the
    /// popup — so the click arrived describing nothing and nothing happened.
    /// </summary>
    private void OnWorkspacesMenuOpenClick(object? sender, RoutedEventArgs e)
    {
        OpenWorkspaceRequested?.Invoke(sender, e);
        WorkspacesMenuButton.Flyout?.Hide();
    }

    private void OnWorkspacesMenuCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: LauncherWorkspaceViewModel { CanClose: true } })
        {
            return;
        }

        CloseWorkspaceRequested?.Invoke(sender, e);
        WorkspacesMenuButton.Flyout?.Hide();
    }


    private void OnShowNewPanelClick(object? sender, RoutedEventArgs e) =>
        ShowNewPanelRequested?.Invoke(sender, e);

    private void OnAddPanelLeftClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        AddPanelToSideRequested?.Invoke(sender, PanelSide.Left);
    }

    private void OnAddPanelRightClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        AddPanelToSideRequested?.Invoke(sender, PanelSide.Right);
    }

    private void OnAddPanelTopClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        AddPanelToSideRequested?.Invoke(sender, PanelSide.Top);
    }

    private void OnAddPanelBottomClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        AddPanelToSideRequested?.Invoke(sender, PanelSide.Bottom);
    }

    private void OnLockShellClick(object? sender, RoutedEventArgs e) =>
        LockShellRequested?.Invoke(sender, e);

    private void OnShowSettingsClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsRequested?.Invoke(sender, e);

    private void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        SubmitAgentQuestionRequested?.Invoke(sender, e);

    private void OnToggleFileTransferManagerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        FileTransferManager.IsVisible = !FileTransferManager.IsVisible;
        if (FileTransferManager.IsVisible)
        {
            FileTransferManagerCloseButton.Focus(NavigationMethod.Tab);
            return;
        }

        FileTransferManagerButton.Focus(NavigationMethod.Tab);
    }

    private void OnToggleAgentClick(object? sender, RoutedEventArgs e) =>
        ToggleAgentRequested?.Invoke(sender, e);

    private void OnToggleAgentPinClick(object? sender, RoutedEventArgs e) =>
        ToggleAgentPinRequested?.Invoke(sender, e);
}
