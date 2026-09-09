using Asura.Core;

namespace Asura.Application;

public interface ISessionHostClient
{
    ValueTask<HostResult<HostHello>> NegotiateAsync(
        ClientHello request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<SessionSnapshot>> EnsureTerminalSessionAsync(
        EnsureTerminalSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<SessionSnapshot>> EnsureBrowserSessionAsync(
        EnsureBrowserSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<SessionSnapshot>("Browser sessions are not implemented by this client.");

    ValueTask<HostResult<SessionSnapshot>> EnsureFilePanelSessionAsync(
        EnsureFilePanelSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<SessionSnapshot>("File-panel sessions are not implemented by this client.");

    ValueTask<HostResult<SessionSnapshot>> EnsureStatisticsSessionAsync(
        EnsureStatisticsSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<SessionSnapshot>("Statistics sessions are not implemented by this client.");

    ValueTask<HostResult<SessionSnapshot>> EnsureProcessMonitorSessionAsync(
        EnsureProcessMonitorSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<SessionSnapshot>("Process-monitor sessions are not implemented by this client.");

    ValueTask<HostResult<SessionSnapshot>> EnsureDatabaseSessionAsync(
        EnsureDatabaseSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<SessionSnapshot>("Database sessions are not implemented by this client.");

    ValueTask<HostResult<SessionSnapshot>> EnsureDockerSessionAsync(
        EnsureDockerSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<SessionSnapshot>("Docker sessions are not implemented by this client.");

    ValueTask<HostResult<SessionSnapshot>> EnsureGitSessionAsync(
        EnsureGitSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<SessionSnapshot>("Git sessions are not implemented by this client.");

    ValueTask<HostResult<WorkspaceGraphSnapshot>> RegisterWorkspaceGraphAsync(
        RegisterWorkspaceGraphRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<WorkspaceGraphSnapshot>("Workspace graph registration is not implemented by this client.");

    ValueTask<HostResult<Unit>> UnregisterWorkspaceGraphAsync(
        UnregisterWorkspaceGraphRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<Unit>("Workspace graph removal is not implemented by this client.");

    ValueTask<HostResult<WorkspaceGraphSnapshot>> GetWorkspaceGraphAsync(
        WorkspaceInstanceId workspaceId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<WorkspaceGraphSnapshot>("Workspace graph queries are not implemented by this client.");

    ValueTask<HostResult<AgentContextSnapshot>> InspectAgentContextAsync(
        AgentContextRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<AgentContextSnapshot>("Agent context inspection is not implemented by this client.");

    ValueTask<HostResult<WorkspaceGraphSnapshot>> ActivateWorkspaceTabAsync(
        ActivateWorkspaceTabRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<WorkspaceGraphSnapshot>("Workspace tab activation is not implemented by this client.");

    ValueTask<HostResult<WorkspaceGraphSnapshot>> ActivateWorkspacePanelAsync(
        ActivateWorkspacePanelRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<WorkspaceGraphSnapshot>("Workspace panel activation is not implemented by this client.");

    ValueTask<HostResult<WorkspaceGraphTransferReceipt>> TransferWorkspaceTabAsync(
        TransferWorkspaceTabRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<WorkspaceGraphTransferReceipt>("Workspace tab transfer is not implemented by this client.");

    ValueTask<HostResult<WorkspaceGraphTransferReceipt>> TransferWorkspacePanelAsync(
        TransferWorkspacePanelRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<WorkspaceGraphTransferReceipt>("Workspace panel transfer is not implemented by this client.");

    IAsyncEnumerable<WorkspaceGraphStreamItem> WatchWorkspaceGraphAsync(
        WatchWorkspaceGraphRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        EmptyWorkspaceGraphStream();

    ValueTask<HostResult<AttachmentResult>> AttachAsync(
        AttachSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<Unit>> AttachTerminalRendererAsync(
        AttachTerminalRendererRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reconfigures a live terminal's typography and palette in place. A restart
    /// would apply the same values, but would take the session's scrollback with
    /// it, so a font change must not be one.
    /// </summary>
    ValueTask<HostResult<bool>> UpdateTerminalRenderProfileAsync(
        UpdateTerminalRenderProfileRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(HostResult<bool>.Succeed(false, 0));

    ValueTask<HostResult<Unit>> AttachBrowserRendererAsync(
        AttachBrowserRendererRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<Unit>("Browser-renderer attachment is not implemented by this client.");

    ValueTask<HostResult<Unit>> DetachAsync(
        DetachSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<SessionSnapshot>> GetSnapshotAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken);

    IAsyncEnumerable<SessionStreamItem> WatchAsync(
        WatchSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// A session's requests to be noticed, carried separately from its
    /// lifecycle stream: a notification is a moment rather than a state, and
    /// the two are watched by different parts of the shell for different
    /// reasons — most importantly, this one keeps running for a workspace that
    /// is not the one on screen.
    /// </summary>
    IAsyncEnumerable<PanelNotificationEvent> WatchNotificationsAsync(
        WatchSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        EmptyAsyncEnumerable<PanelNotificationEvent>.Instance;

    ValueTask<HostResult<InputLeaseDecision>> AcquireInputLeaseAsync(
        AcquireInputLeaseRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<Unit>> ReleaseInputLeaseAsync(
        ReleaseInputLeaseRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<Unit>> FocusTerminalAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<Unit>> BlurTerminalAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<Unit>("Terminal focus-loss reporting is not implemented by this client.");

    ValueTask<HostResult<Unit>> ResizeTerminalAsync(
        TerminalResizeRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<Unit>> WriteTerminalAsync(
        TerminalWriteRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<Unit>> SendTerminalKeyAsync(
        TerminalKeyRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<Unit>> SendTerminalPhysicalKeyAsync(
        TerminalPhysicalKeyRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<Unit>("Physical terminal keyboard events are not implemented by this client.");

    ValueTask<HostResult<Unit>> EnterTerminalAsync(
        TerminalEnterRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<Unit>("Explicit terminal Enter is not implemented by this client.");

    ValueTask<HostResult<Unit>> InterruptTerminalAsync(
        TerminalInterruptRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<Unit>("Terminal interrupt is not implemented by this client.");

    ValueTask<HostResult<Unit>> SendTerminalMouseAsync(
        TerminalMouseRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<Unit>> ScrollTerminalViewportAsync(
        TerminalViewportScrollRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<Unit>> ClearTerminalScrollbackAsync(
        TerminalClearScrollbackRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<Unit>("Clearing terminal scrollback is not implemented by this client.");

    ValueTask<HostResult<TerminalFindResult>> FindTerminalAsync(
        TerminalFindRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<TerminalFindResult>("Finding terminal output is not implemented by this client.");

    ValueTask<HostResult<Unit>> UpdateTerminalSelectionAsync(
        TerminalSelectionRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<TerminalSelectionText>> ReadTerminalSelectionAsync(
        TerminalSelectionReadRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<TerminalPasteResult>> PasteTerminalAsync(
        TerminalPasteRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<TerminalScreenSnapshot>> ReadTerminalScreenAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the renderer-only terminal frame. This remains a separate port
    /// from the bounded text snapshot used by agents and recovery.
    /// </summary>
    ValueTask<HostResult<TerminalRenderFrame>> ReadTerminalRenderFrameAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<TerminalRenderFrame>(
            "Managed terminal render frames are not implemented by this client.");

    ValueTask<HostResult<TerminalWaitOutcome>> WaitForTerminalTextAsync(
        TerminalWaitForTextRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<TerminalWaitOutcome>("Terminal text waits are not implemented by this client.");

    ValueTask<HostResult<TerminalWaitOutcome>> WaitForTerminalChangeAsync(
        TerminalWaitForChangeRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<TerminalWaitOutcome>("Terminal change waits are not implemented by this client.");

    ValueTask<HostResult<TerminalWaitOutcome>> WaitForTerminalStableAsync(
        TerminalWaitForStableRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<TerminalWaitOutcome>("Terminal stability waits are not implemented by this client.");

    ValueTask<HostResult<BrowserResult<BrowserSessionState>>> ReadBrowserStateAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<BrowserResult<BrowserSessionState>>(
            "Browser state is not implemented by this client.");

    ValueTask<HostResult<BrowserResult<BrowserSessionState>>> NavigateBrowserAsync(
        BrowserNavigateRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<BrowserResult<BrowserSessionState>>(
            "Browser navigation is not implemented by this client.");

    ValueTask<HostResult<BrowserResult<BrowserSessionState>>> GoBackBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<BrowserResult<BrowserSessionState>>(
            "Browser back navigation is not implemented by this client.");

    ValueTask<HostResult<BrowserResult<BrowserSessionState>>> GoForwardBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<BrowserResult<BrowserSessionState>>(
            "Browser forward navigation is not implemented by this client.");

    ValueTask<HostResult<BrowserResult<BrowserSessionState>>> ReloadBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<BrowserResult<BrowserSessionState>>(
            "Browser reload is not implemented by this client.");

    ValueTask<HostResult<BrowserResult<BrowserSessionState>>> StopBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<BrowserResult<BrowserSessionState>>(
            "Browser stop is not implemented by this client.");

    ValueTask<HostResult<MonitorPanelResult<SystemStatisticsSnapshot>>> ReadStatisticsAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<MonitorPanelResult<SystemStatisticsSnapshot>>(
            "Statistics capture is not implemented by this client.");

    ValueTask<HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>> ListProcessesAsync(
        ProcessMonitorHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<MonitorPanelResult<ProcessMonitorSnapshot>>(
            "Process monitoring is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<FilePanelPage>>> ListFilesAsync(
        FilePanelListHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<FilePanelPage>>("File listing is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<FilePanelEntry>>> StatFileAsync(
        FilePanelStatHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<FilePanelEntry>>("File stat is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<FilePanelPreview>>> PreviewFileAsync(
        FilePanelPreviewHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<FilePanelPreview>>("File preview is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<FilePanelEntry>>> CreateFileDirectoryAsync(
        FilePanelCreateDirectoryHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<FilePanelEntry>>("Directory creation is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<FilePanelEntry>>> RenameFileAsync(
        FilePanelRenameHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<FilePanelEntry>>("File rename is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<FilePanelDeleteReceipt>>> DeleteFileAsync(
        FilePanelDeleteHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<FilePanelDeleteReceipt>>("File deletion is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<FilePanelAccessControl>>> GetFileAccessControlAsync(
        FilePanelAccessControlHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<FilePanelAccessControl>>(
            "Reading file access control is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<FilePanelAccessControl>>> SetFileAccessControlAsync(
        FilePanelSetAccessControlHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<FilePanelAccessControl>>(
            "Changing file access control is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<FilePanelTransferSnapshot>>> EnqueueFileTransferAsync(
        FilePanelTransferEnqueueHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<FilePanelTransferSnapshot>>(
            "File-transfer enqueue is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<Unit>>> CancelFileTransferAsync(
        FilePanelTransferCancelHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<Unit>>("File-transfer cancellation is not implemented by this client.");

    ValueTask<HostResult<FilePanelResult<FilePanelTransferSnapshot>>> RetryFileTransferAsync(
        FilePanelTransferRetryHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken) =>
        Unsupported<FilePanelResult<FilePanelTransferSnapshot>>(
            "File-transfer retry is not implemented by this client.");

    ValueTask<HostResult<CloseScopeResult>> CloseAsync(
        CloseScopeRequest request,
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<HostResult<Unit>> DisconnectClientAsync(
        ClientId clientId,
        OperationContext context,
        CancellationToken cancellationToken);

    private static ValueTask<HostResult<T>> Unsupported<T>(string message) =>
        ValueTask.FromResult(HostResult<T>.Fail(
            HostError.Create(HostErrorCode.CapabilityNotSupported, message),
            0));

    private static async IAsyncEnumerable<WorkspaceGraphStreamItem> EmptyWorkspaceGraphStream()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
