using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

internal sealed class FakeTerminalSession(
    SessionId id,
    TerminalLaunchRequest launch,
    bool hasActiveWork,
    PanelCloseOutcome? closeOutcomeOverride,
    bool throwWhenClosing) : ITerminalPanelSession
{
    private readonly Channel<PanelNotificationEvent> _notifications =
        Channel.CreateUnbounded<PanelNotificationEvent>();
    private bool _closed;
    private bool _rendererAttached;

    public SessionId Id { get; } = id;

    public TerminalLaunchRequest Launch { get; } = launch;

    public PanelKind Kind => PanelKind.Terminal;

    public CapabilitySet Capabilities { get; private set; } = new(
    [
        SessionCapabilities.NativeRenderer,
        SessionCapabilities.TerminalAgentInputBarrier,
        SessionCapabilities.TerminalReadScreen,
        SessionCapabilities.TerminalWrite,
        SessionCapabilities.TerminalSendKeys,
        SessionCapabilities.TerminalSendChord,
        SessionCapabilities.TerminalEnter,
        SessionCapabilities.TerminalInterrupt,
        SessionCapabilities.TerminalWait,
        SessionCapabilities.TerminalMouse,
        SessionCapabilities.TerminalRevisionBoundMouse,
        SessionCapabilities.TerminalScrollback,
        SessionCapabilities.TerminalScrollbackRead,
        SessionCapabilities.TerminalScrollbackFind,
        SessionCapabilities.TerminalRenderedHistory,
        SessionCapabilities.TerminalClearScrollback,
        SessionCapabilities.TerminalFind,
        SessionCapabilities.TerminalSelection,
        SessionCapabilities.TerminalPaste,
        SessionCapabilities.TerminalResize,
        SessionCapabilities.TerminalFocus,
    ]);

    public bool HasActiveWork { get; set; } = hasActiveWork;

    public bool IsClosed => _closed;

    public Func<CancellationToken, ValueTask>? BeforeSnapshotAsync { get; set; }

    public bool RendererAttached => _rendererAttached;

    public int AttachRendererCount { get; private set; }

    public NativeRendererHost? LastRendererHost { get; private set; }

    public int DetachRendererCount { get; private set; }

    public bool BlockRendererDetach { get; set; }

    public bool ThrowAfterRendererDetach { get; set; }

    public TaskCompletionSource RendererDetachStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseRendererDetach { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public int CloseCount { get; private set; }

    public int WriteCount { get; private set; }

    public string? LastWrittenText { get; private set; }

    public bool BlockWrites { get; set; }

    public TaskCompletionSource WriteStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseWrite { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public bool BlockTextWaits { get; set; }

    public TaskCompletionSource TextWaitStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseTextWait { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public PanelCloseMode? LastCloseMode { get; private set; }

    public TerminalKeyStroke? LastKeyStroke { get; private set; }

    public TerminalPhysicalKeyEvent? LastPhysicalKeyEvent { get; private set; }

    public int FocusCount { get; private set; }

    public int BlurCount { get; private set; }

    public TerminalCharacterChord? LastChord { get; private set; }

    public int ChordCount { get; private set; }

    public bool BlockChords { get; set; }

    public bool IgnoreChordCancellationAfterStart { get; set; }

    public TaskCompletionSource ChordStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseChord { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public int EnterCount { get; private set; }

    public int InterruptCount { get; private set; }

    public int ResizeCount { get; private set; }

    public ViewportDescriptor? LastResizeViewport { get; private set; }

    public bool BlockResizes { get; set; }

    public bool IgnoreResizeCancellationAfterStart { get; set; }

    public TaskCompletionSource ResizeStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseResize { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TerminalMouseInput? LastMouseInput { get; private set; }

    public int MouseCount { get; private set; }

    public bool BlockMouse { get; set; }

    public TaskCompletionSource MouseStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseMouse { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TerminalViewportScrollInput? LastScrollInput { get; private set; }

    public int ClearScrollbackCount { get; private set; }

    public TerminalFindInput? LastFindInput { get; private set; }

    public TerminalSelectionInput? LastSelectionInput { get; private set; }

    public TerminalPasteInput? LastPasteInput { get; private set; }

    public int PasteCount { get; private set; }

    public int SubmitTextCount { get; private set; }

    public TerminalPasteResult? PasteResultOverride { get; set; }

    public string ScreenText { get; set; } = "fake screen";

    public string? ScreenWorkingDirectory { get; set; } = "/tmp";

    public long ScreenContentRevision { get; set; }

    public TerminalScreenDiffInput? LastScreenDiffInput { get; private set; }

    public TerminalScreenDiffResult? ScreenDiffResultOverride { get; set; }

    public TerminalWaitOutcome? WaitOutcomeOverride { get; set; }

    public TerminalWaitForTextInput? LastTextWait { get; private set; }

    public TerminalWaitForDelayInput? LastDelayWait { get; private set; }

    public TerminalScrollbackReadInput? LastScrollbackRead { get; private set; }

    public TerminalScrollbackFindInput? LastScrollbackFind { get; private set; }

    public TerminalRenderedHistoryFindInput? LastRenderedHistoryFind { get; private set; }

    public TerminalRenderedHistoryRowAnchor? LastRenderedHistoryJump { get; private set; }

    public TerminalWaitForChangeInput? LastChangeWait { get; private set; }

    public TerminalWaitForStableInput? LastStableWait { get; private set; }

    public TerminalWaitForPromptReadyInput? LastPromptReadyWait { get; private set; }

    public TerminalWaitForCommandFinishedInput? LastCommandFinishedWait { get; private set; }

    public bool SupportsShellIntegrationEvents { get; set; } = true;

    public int? SemanticWaitExitCode { get; set; } = 17;

    public TaskCompletionSource NotificationWatchStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public void PublishNotification(PanelNotificationEvent notification)
    {
        if (!_notifications.Writer.TryWrite(notification))
        {
            throw new InvalidOperationException("The notification stream is closed.");
        }
    }

    public ValueTask AttachRendererAsync(
        NativeRendererHost rendererHost,
        CancellationToken cancellationToken)
    {
        LastRendererHost = rendererHost;
        cancellationToken.ThrowIfCancellationRequested();
        _rendererAttached = true;
        AttachRendererCount++;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DetachRendererAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _rendererAttached = false;
        DetachRendererCount++;
        RendererDetachStarted.TrySetResult();
        if (BlockRendererDetach)
        {
            await ReleaseRendererDetach.Task.ConfigureAwait(false);
        }

        if (ThrowAfterRendererDetach)
        {
            throw new InvalidOperationException("fake renderer detach failure");
        }
    }

    public ValueTask FocusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FocusCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask BlurAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlurCount++;
        return ValueTask.CompletedTask;
    }

    public async ValueTask ResizeAsync(
        ViewportDescriptor viewport,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastResizeViewport = viewport;
        ResizeCount++;
        ResizeStarted.TrySetResult();
        if (BlockResizes)
        {
            if (IgnoreResizeCancellationAfterStart)
            {
                await ReleaseResize.Task;
            }
            else
            {
                await ReleaseResize.Task.WaitAsync(cancellationToken);
            }
        }
    }

    public async ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastWrittenText = text;
        WriteCount++;
        WriteStarted.TrySetResult();
        if (BlockWrites)
        {
            await ReleaseWrite.Task.WaitAsync(cancellationToken);
        }
    }

    public ValueTask SendKeyAsync(
        TerminalKeyStroke keyStroke,
        CancellationToken cancellationToken)
    {
        LastKeyStroke = keyStroke;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask SendPhysicalKeyAsync(
        TerminalPhysicalKeyEvent keyEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyEvent);
        cancellationToken.ThrowIfCancellationRequested();
        LastPhysicalKeyEvent = keyEvent;
        return ValueTask.CompletedTask;
    }

    public async ValueTask SendChordAsync(
        TerminalCharacterChord chord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chord);
        cancellationToken.ThrowIfCancellationRequested();
        ChordCount++;
        ChordStarted.TrySetResult();
        if (BlockChords)
        {
            if (IgnoreChordCancellationAfterStart)
            {
                await ReleaseChord.Task;
            }
            else
            {
                await ReleaseChord.Task.WaitAsync(cancellationToken);
            }
        }

        LastChord = chord;
    }

    public ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnterCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask InterruptAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InterruptCount++;
        return ValueTask.CompletedTask;
    }

    public async ValueTask SendMouseAsync(
        TerminalMouseInput mouseInput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastMouseInput = mouseInput;
        MouseCount++;
        MouseStarted.TrySetResult();
        if (BlockMouse)
        {
            await ReleaseMouse.Task.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask<TerminalRevisionBoundMouseOutcome>
        SendMouseAtContentRevisionAsync(
            TerminalMouseInput mouseInput,
            long expectedContentRevision,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedContentRevision != ScreenContentRevision)
        {
            return TerminalRevisionBoundMouseOutcome.ContentRevisionChanged;
        }

        if (mouseInput.Column >= 80 || mouseInput.Row >= 24)
        {
            return TerminalRevisionBoundMouseOutcome.CoordinatesOutOfBounds;
        }

        await SendMouseAsync(mouseInput, cancellationToken);
        return TerminalRevisionBoundMouseOutcome.Sent;
    }

    public void RemoveCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        Capabilities = new CapabilitySet(
            Capabilities.Values.Where(value =>
                !string.Equals(value, capability, StringComparison.Ordinal)));
    }

    public ValueTask ScrollViewportAsync(
        TerminalViewportScrollInput scrollInput,
        CancellationToken cancellationToken)
    {
        LastScrollInput = scrollInput;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<TerminalScrollbackSnapshot> ReadScrollbackAsync(
        TerminalScrollbackReadInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastScrollbackRead = input;
        var row = new TerminalScrollbackRow(
            new TerminalScrollbackRowAnchor(ScreenContentRevision, 0),
            ScreenText);
        return ValueTask.FromResult(new TerminalScrollbackSnapshot(
            [row],
            TotalLines: 1,
            ContentRevision: ScreenContentRevision,
            HasMoreBefore: false,
            HasMoreAfter: false));
    }

    public ValueTask<TerminalScrollbackFindResult> FindScrollbackAsync(
        TerminalScrollbackFindInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastScrollbackFind = input;
        IReadOnlyList<TerminalScrollbackRow> matches = ScreenText.Contains(
            input.Query,
            StringComparison.Ordinal)
                ?
                [
                    new TerminalScrollbackRow(
                        new TerminalScrollbackRowAnchor(
                            ScreenContentRevision,
                            0),
                        ScreenText),
                ]
                : [];
        return ValueTask.FromResult(new TerminalScrollbackFindResult(
            matches,
            TotalLines: 1,
            ContentRevision: ScreenContentRevision,
            IsTruncated: false));
    }

    public ValueTask<TerminalRenderedHistoryFindResult> FindRenderedHistoryAsync(
        TerminalRenderedHistoryFindInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRenderedHistoryFind = input;
        IReadOnlyList<TerminalRenderedHistoryRow> matches = ScreenText.Contains(
            input.Query,
            StringComparison.Ordinal)
                ?
                [
                    new TerminalRenderedHistoryRow(
                        new TerminalRenderedHistoryRowAnchor(
                            ScreenContentRevision,
                            2),
                        ScreenText),
                ]
                : [];
        return ValueTask.FromResult(new TerminalRenderedHistoryFindResult(
            matches,
            TotalRows: 24,
            ContentRevision: ScreenContentRevision,
            IsTruncated: false));
    }

    public ValueTask JumpToRenderedHistoryAsync(
        TerminalRenderedHistoryRowAnchor anchor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (anchor.ContentRevision != ScreenContentRevision)
        {
            throw new TerminalRenderedHistoryAnchorStaleException(
                anchor.ContentRevision,
                ScreenContentRevision);
        }

        LastRenderedHistoryJump = anchor;
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearScrollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearScrollbackCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TerminalFindResult> FindAsync(
        TerminalFindInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastFindInput = input;
        return ValueTask.FromResult(input.Query.Length == 0
            ? TerminalFindResult.Empty
            : new TerminalFindResult(2, Math.Abs(input.RequestedMatchIndex % 2), false));
    }

    public ValueTask UpdateSelectionAsync(
        TerminalSelectionInput selectionInput,
        CancellationToken cancellationToken)
    {
        LastSelectionInput = selectionInput;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<TerminalSelectionText> ReadSelectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TerminalSelectionText("selected", true, false));
    }

    public ValueTask<TerminalPasteResult> PasteAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken)
    {
        LastPasteInput = pasteInput;
        cancellationToken.ThrowIfCancellationRequested();
        PasteCount++;
        return ValueTask.FromResult(
            PasteResultOverride
            ?? TerminalPasteResult.Completed(bracketed: false));
    }

    public ValueTask<TerminalPasteResult> SubmitTextAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken)
    {
        LastPasteInput = pasteInput;
        cancellationToken.ThrowIfCancellationRequested();
        SubmitTextCount++;
        return ValueTask.FromResult(
            PasteResultOverride
            ?? TerminalPasteResult.Completed(bracketed: false));
    }

    public ValueTask<TerminalScreenSnapshot> ReadScreenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TerminalScreenSnapshot(
            ScreenText,
            1,
            1,
            24,
            80,
            false,
            ScreenWorkingDirectory,
            DateTimeOffset.UnixEpoch,
            ContentRevision: ScreenContentRevision));
    }

    public ValueTask<TerminalScreenDiffResult> ReadScreenDiffAsync(
        TerminalScreenDiffInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastScreenDiffInput = input;
        return ValueTask.FromResult(
            ScreenDiffResultOverride
            ?? new TerminalScreenDiffResult(
                input.AfterContentRevision,
                ScreenContentRevision,
                BaselineAvailable:
                    input.AfterContentRevision == ScreenContentRevision,
                ChangedRows: [],
                IsTruncated: false,
                CursorRow: 1,
                CursorColumn: 1,
                IsCursorVisible: true,
                InteractiveState: null));
    }

    public async ValueTask<TerminalWaitOutcome> WaitForTextAsync(
        TerminalWaitForTextInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastTextWait = input;
        TextWaitStarted.TrySetResult();
        if (BlockTextWaits)
        {
            await ReleaseTextWait.Task.WaitAsync(cancellationToken);
        }

        var snapshot = ReadScreenAsync(cancellationToken).Result;
        return WaitOutcomeOverride
            ?? (snapshot.PlainText.Contains(input.Text, StringComparison.Ordinal)
                ? TerminalWaitOutcome.Matched(snapshot, snapshot.ContentRevision)
                : TerminalWaitOutcome.Timeout(snapshot, snapshot.ContentRevision));
    }

    public ValueTask<TerminalWaitOutcome> WaitForDelayAsync(
        TerminalWaitForDelayInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastDelayWait = input;
        var snapshot = ReadScreenAsync(cancellationToken).Result;
        return ValueTask.FromResult(
            WaitOutcomeOverride
            ?? TerminalWaitOutcome.Elapsed(
                snapshot,
                snapshot.ContentRevision));
    }

    public ValueTask<TerminalWaitOutcome> WaitForChangeAsync(
        TerminalWaitForChangeInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastChangeWait = input;
        var snapshot = ReadScreenAsync(cancellationToken).Result;
        return ValueTask.FromResult(WaitOutcomeOverride
            ?? (snapshot.ContentRevision > input.AfterContentRevision
                ? TerminalWaitOutcome.Changed(snapshot, input.AfterContentRevision)
                : TerminalWaitOutcome.Timeout(snapshot, input.AfterContentRevision)));
    }

    public ValueTask<TerminalWaitOutcome> WaitForStableAsync(
        TerminalWaitForStableInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastStableWait = input;
        var snapshot = ReadScreenAsync(cancellationToken).Result;
        return ValueTask.FromResult(WaitOutcomeOverride
            ?? TerminalWaitOutcome.Stable(snapshot, snapshot.ContentRevision));
    }

    public ValueTask<TerminalWaitOutcome> WaitForPromptReadyAsync(
        TerminalWaitForPromptReadyInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPromptReadyWait = input;
        return ValueTask.FromResult(SemanticWait(
            input.AfterShellEventSequence,
            TerminalCommandBoundaryKind.CommandInputStarted));
    }

    public ValueTask<TerminalWaitOutcome> WaitForCommandFinishedAsync(
        TerminalWaitForCommandFinishedInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastCommandFinishedWait = input;
        return ValueTask.FromResult(SemanticWait(
            input.AfterShellEventSequence,
            TerminalCommandBoundaryKind.CommandFinished));
    }

    private TerminalWaitOutcome SemanticWait(
        long afterShellEventSequence,
        TerminalCommandBoundaryKind eventKind)
    {
        if (!SupportsShellIntegrationEvents)
        {
            return TerminalWaitOutcome.Unsupported();
        }

        if (WaitOutcomeOverride is not null)
        {
            return WaitOutcomeOverride;
        }

        if (afterShellEventSequence == long.MaxValue)
        {
            var final = ReadScreenAsync(CancellationToken.None).Result;
            return TerminalWaitOutcome.Timeout(
                final,
                final.ContentRevision);
        }

        var shellEvent = new TerminalShellIntegrationEvent(
            afterShellEventSequence + 1,
            eventKind,
            DateTimeOffset.UnixEpoch,
            eventKind == TerminalCommandBoundaryKind.CommandFinished
                ? SemanticWaitExitCode
                : null);
        var snapshot = new TerminalScreenSnapshot(
            ScreenText,
            CursorRow: 1,
            CursorColumn: 1,
            Rows: 24,
            Columns: 80,
            IsAlternateScreen: false,
            WorkingDirectory: ScreenWorkingDirectory,
            CapturedAtUtc: DateTimeOffset.UnixEpoch,
            ContentRevision: ScreenContentRevision,
            ShellIntegrationEvents: [shellEvent]);
        return eventKind == TerminalCommandBoundaryKind.CommandInputStarted
            ? TerminalWaitOutcome.PromptReady(
                snapshot,
                snapshot.ContentRevision,
                shellEvent)
            : TerminalWaitOutcome.CommandFinished(
                snapshot,
                snapshot.ContentRevision,
                shellEvent);
    }

    public async ValueTask<PanelSessionSnapshot> SnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (BeforeSnapshotAsync is { } beforeSnapshot)
        {
            await beforeSnapshot(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _closed
            ? new PanelSessionSnapshot(
                SessionLifecycle.Closed,
                SessionHealth.Ended,
                false,
                "closed")
            : new PanelSessionSnapshot(
                _rendererAttached ? SessionLifecycle.Active : SessionLifecycle.Starting,
                _rendererAttached ? SessionHealth.Healthy : SessionHealth.Starting,
                HasActiveWork,
                HasActiveWork ? "foreground process" : "ready");
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = afterSequence;
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    public async IAsyncEnumerable<PanelNotificationEvent> WatchNotificationsAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        NotificationWatchStarted.TrySetResult();
        await foreach (var notification in _notifications.Reader
            .ReadAllAsync(cancellationToken))
        {
            if (notification.Sequence > afterSequence)
            {
                yield return notification;
            }
        }
    }

    public ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(PanelCloseOutcome.Cancelled);
        }

        CloseCount++;
        LastCloseMode = mode;
        if (throwWhenClosing)
        {
            throw new InvalidOperationException("fake close failure");
        }

        var outcome = closeOutcomeOverride
            ?? (HasActiveWork && mode == PanelCloseMode.Graceful
                ? PanelCloseOutcome.ConfirmationRequired
                : mode == PanelCloseMode.Force
                    ? PanelCloseOutcome.ForceTerminated
                    : PanelCloseOutcome.GracefullyClosed);
        if (outcome is PanelCloseOutcome.GracefullyClosed
            or PanelCloseOutcome.ForceTerminated
            or PanelCloseOutcome.AlreadyClosed)
        {
            _closed = true;
            _rendererAttached = false;
            HasActiveWork = false;
        }

        return ValueTask.FromResult(outcome);
    }

    public ValueTask DisposeAsync()
    {
        _closed = true;
        _rendererAttached = false;
        _notifications.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
