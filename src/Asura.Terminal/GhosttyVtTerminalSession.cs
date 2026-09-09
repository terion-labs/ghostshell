using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Asura.Application;
using Asura.Core;
using Asura.Terminal.GhosttyVt;

namespace Asura.Terminal;

/// <summary>
/// Cross-platform terminal session whose one canonical VT state is owned by
/// libghostty-vt and whose presentation is drawn by Asura/Avalonia.
/// </summary>
internal sealed partial class GhosttyVtTerminalSession : ITerminalPanelSession
{
    private readonly object _gate = new();
    private readonly TerminalLaunchRequest _launch;
    private readonly IPortablePtyConnection _pty;
    private readonly bool _shellIntegrationApplied;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<QueuedTerminalInput> _writes =
        Channel.CreateBounded<QueuedTerminalInput>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
    private readonly Channel<PanelSessionEvent> _events =
        Channel.CreateBounded<PanelSessionEvent>(
            new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
    /// <summary>
    /// Requests to be noticed, kept apart from <see cref="_events"/>.
    ///
    /// Both item count and retained UTF-8 bytes are bounded. Wait mode is used
    /// with non-blocking TryWrite so a rejected item is observable to the byte
    /// accounting instead of being invisibly evicted by the channel.
    /// </summary>
    private readonly Channel<QueuedPanelNotification> _notifications =
        Channel.CreateBounded<QueuedPanelNotification>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
    internal const long MaximumQueuedNotificationUtf8Bytes = 256 * 1024;
    private long _queuedNotificationUtf8Bytes;
    private long _notificationSequence;
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<TerminalKittyImageKey, TerminalKittyImageContent> _kittyImages = [];
    private readonly List<SemanticMarker> _semanticMarkers = [];

    private readonly GhosttyVtTerminalHandle _terminal;
    private readonly GhosttyVtRenderStateHandle _renderState;
    private readonly GhosttyVtRenderRowIteratorHandle _rowIterator;
    private readonly GhosttyVtRenderRowCellsHandle _rowCells;
    private readonly GhosttyVtKeyEventHandle _keyEvent;
    private readonly GhosttyVtKeyEncoderHandle _keyEncoder;
    private readonly GhosttyVtMouseEventHandle _mouseEvent;
    private readonly GhosttyVtMouseEncoderHandle _mouseEncoder;
    private readonly GhosttyVtKittyPlacementIteratorHandle _kittyPlacementIterator;
    private readonly GhosttyVtKittyVirtualPlacementIteratorHandle _kittyVirtualPlacementIterator;
    private readonly Task _readerTask;
    private readonly Task _writerTask;
    private GCHandle _selfHandle;

    private SessionFailure? _failure;
    private TerminalRenderProfileSnapshot _renderProfile;
    private TerminalRenderFrame? _cachedRenderFrame;
    private SelectionAnchor? _selectionAnchor;
    private bool _rendererAttached;
    private bool _processExited;
    private bool _closed;
    private bool _protocolResponseOverflow;
    private int? _exitCode;
    private int _columns;
    private int _rows;
    private uint _cellWidthPixels = 8;
    private uint _cellHeightPixels = 16;
    private double _renderScale = 1;
    private long _sequence;
    private long _contentRevision;
    private long _renderRevision;
    private long _renderedContentRevision = -1;
    private long _deliveredRenderRevision = -1;
    private long _commandBoundarySequence;
    private long _interactiveStateSequence;
    private TerminalInteractiveStateSnapshot? _interactiveState;
    private TerminalScreenSnapshot? _lastObservedScreen;
    private TerminalShellActivityState _shellActivity;

    internal GhosttyVtTerminalSession(
        SessionId id,
        TerminalLaunchRequest launch,
        IPortablePtyConnection pty,
        int initialColumns,
        int initialRows,
        bool shellIntegrationApplied)
    {
        Id = id;
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _pty = pty ?? throw new ArgumentNullException(nameof(pty));
        _shellIntegrationApplied = shellIntegrationApplied;
        _renderProfile = launch.RenderProfile ?? CreateFallbackProfile();
        _columns = initialColumns;
        _rows = initialRows;

        // Create the interdependent native resources as one exception-safe unit.
        // SafeHandle finalizers would eventually release a partially constructed
        // set, but a terminal factory failure must not retain native state until
        // an indeterminate future collection.
        var nativeHandles = CreateNativeHandles(initialColumns, initialRows);
        _terminal = nativeHandles.Terminal;
        _renderState = nativeHandles.RenderState;
        _rowIterator = nativeHandles.RowIterator;
        _rowCells = nativeHandles.RowCells;
        _keyEvent = nativeHandles.KeyEvent;
        _keyEncoder = nativeHandles.KeyEncoder;
        _mouseEvent = nativeHandles.MouseEvent;
        _mouseEncoder = nativeHandles.MouseEncoder;
        _kittyPlacementIterator = nativeHandles.KittyPlacementIterator;
        _kittyVirtualPlacementIterator = nativeHandles.KittyVirtualPlacementIterator;

        try
        {
            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            lock (_gate)
            {
                ConfigureTerminalUnsafe(_renderProfile);
                PublishUnsafe(
                    SessionLifecycle.Active,
                    SessionHealth.Healthy,
                    "libghostty-vt and portable PTY started.");
            }

            _pty.ProcessExited += OnProcessExited;
            _readerTask = ReadLoopAsync(_lifetime.Token);
            _writerTask = WriteLoopAsync(_lifetime.Token);
            if (launch.InitialCommand is { } initialCommand)
            {
                // Queued before any user input can arrive, so the command is the
                // first line the shell reads. The PTY buffers it until the shell
                // is ready, which keeps the mechanism uniform across local and
                // remote endpoints.
                _writes.Writer.TryWrite(new QueuedTerminalInput(
                    System.Text.Encoding.UTF8.GetBytes(initialCommand + "\n"),
                    _lifetime.Token));
            }

            TryMarkKnownProcessExit();
        }
        catch (Exception exception)
        {
            var original = ExceptionDispatchInfo.Capture(exception);
            try
            {
                DisposeNativeHandles();
            }
            catch
            {
                // A constructor failure remains the primary diagnostic. All
                // SafeHandles have nevertheless been given a release attempt.
            }

            try
            {
                if (_selfHandle.IsAllocated)
                {
                    _selfHandle.Free();
                }
            }
            catch
            {
            }

            original.Throw();
            throw;
        }
    }

    internal static CapabilitySet SessionCapabilities { get; } = new(
    [
        Asura.Application.SessionCapabilities.ManagedRenderer,
        Asura.Application.SessionCapabilities.TerminalAgentInputBarrier,
        Asura.Application.SessionCapabilities.TerminalReadScreen,
        Asura.Application.SessionCapabilities.TerminalWrite,
        Asura.Application.SessionCapabilities.TerminalSendKeys,
        Asura.Application.SessionCapabilities.TerminalSendChord,
        Asura.Application.SessionCapabilities.TerminalEnter,
        Asura.Application.SessionCapabilities.TerminalInterrupt,
        Asura.Application.SessionCapabilities.TerminalWait,
        Asura.Application.SessionCapabilities.TerminalMouse,
        Asura.Application.SessionCapabilities.TerminalScrollback,
        Asura.Application.SessionCapabilities.TerminalScrollbackRead,
        Asura.Application.SessionCapabilities.TerminalScrollbackFind,
        Asura.Application.SessionCapabilities.TerminalRenderedHistory,
        Asura.Application.SessionCapabilities.TerminalRevisionBoundMouse,
        Asura.Application.SessionCapabilities.TerminalFind,
        Asura.Application.SessionCapabilities.TerminalSelection,
        Asura.Application.SessionCapabilities.TerminalPaste,
        Asura.Application.SessionCapabilities.TerminalResize,
        Asura.Application.SessionCapabilities.TerminalFocus,
    ]);

    public SessionId Id { get; }

    public TerminalLaunchRequest Launch => _launch;

    public PanelKind Kind => PanelKind.Terminal;

    public CapabilitySet Capabilities => SessionCapabilities;

    public ValueTask AttachRendererAsync(
        NativeRendererHost rendererHost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rendererHost);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(rendererHost.HandleDescriptor, "Asura.Managed", StringComparison.Ordinal))
        {
            return ValueTask.FromException(new PlatformNotSupportedException(
                "The libghostty-vt engine is presented by Asura's managed renderer."));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _rendererAttached = true;
            ResizeUnsafe(rendererHost.Viewport);
            PublishUnsafe(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                "Managed libghostty-vt renderer attached.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DetachRendererAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _rendererAttached = false;
            if (!_closed)
            {
                PublishUnsafe(
                    SessionLifecycle.Active,
                    SessionHealth.Healthy,
                    "Managed terminal renderer detached.");
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(
        ViewportDescriptor viewport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            ResizeUnsafe(viewport);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> UpdateRenderProfileAsync(
        TerminalRenderProfileSnapshot renderProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renderProfile);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _renderProfile = renderProfile;
            ConfigurePresentationUnsafe(renderProfile);
            MarkContentChangedUnsafe();
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask<TerminalRenderFrame> ReadRenderFrameAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var frame = BuildRenderFrameUnsafe();
            if (_deliveredRenderRevision == frame.Revision
                && frame.Delta.Kind != TerminalRenderDamageKind.None)
            {
                return ValueTask.FromResult(new TerminalRenderFrame(
                    frame.Revision,
                    frame.Rows,
                    frame.Columns,
                    frame.ViewportRows,
                    frame.Cursor,
                    new TerminalRenderDelta(TerminalRenderDamageKind.None),
                    frame.KittyGraphics));
            }

            _deliveredRenderRevision = frame.Revision;
            return ValueTask.FromResult(frame);
        }
    }

    public ValueTask<TerminalScreenSnapshot> ReadScreenAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var snapshot = BuildScreenSnapshotUnsafe(BuildRenderFrameUnsafe());
            return ValueTask.FromResult(snapshot);
        }
    }

    public ValueTask<TerminalScreenSnapshot> ObserveScreenAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var snapshot = BuildScreenSnapshotUnsafe(BuildRenderFrameUnsafe());
            _lastObservedScreen = snapshot;
            return ValueTask.FromResult(snapshot);
        }
    }

    public ValueTask<TerminalWaitOutcome> WaitForTextAsync(
        TerminalWaitForTextInput input,
        CancellationToken cancellationToken) =>
        TerminalAutomationWaiter.WaitForTextAsync(
            input,
            ObserveScreenAsync,
            SnapshotAsync,
            cancellationToken);

    public ValueTask<TerminalWaitOutcome> WaitForDelayAsync(
        TerminalWaitForDelayInput input,
        CancellationToken cancellationToken) =>
        TerminalAutomationWaiter.WaitForDelayAsync(
            input,
            ObserveScreenAsync,
            SnapshotAsync,
            cancellationToken);

    public ValueTask<TerminalWaitOutcome> WaitForChangeAsync(
        TerminalWaitForChangeInput input,
        CancellationToken cancellationToken) =>
        TerminalAutomationWaiter.WaitForChangeAsync(
            input,
            ObserveScreenAsync,
            SnapshotAsync,
            cancellationToken);

    public ValueTask<TerminalWaitOutcome> WaitForStableAsync(
        TerminalWaitForStableInput input,
        CancellationToken cancellationToken) =>
        TerminalAutomationWaiter.WaitForStableAsync(
            input,
            ObserveScreenAsync,
            SnapshotAsync,
            cancellationToken);

    public ValueTask<TerminalWaitOutcome> WaitForPromptReadyAsync(
        TerminalWaitForPromptReadyInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        return _shellIntegrationApplied
            ? TerminalAutomationWaiter.WaitForPromptReadyAsync(
                input,
                ObserveScreenAsync,
                SnapshotAsync,
                cancellationToken)
            : ValueTask.FromResult(TerminalWaitOutcome.Unsupported());
    }

    public ValueTask<TerminalWaitOutcome> WaitForCommandFinishedAsync(
        TerminalWaitForCommandFinishedInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        return _shellIntegrationApplied
            ? TerminalAutomationWaiter.WaitForCommandFinishedAsync(
                input,
                ObserveScreenAsync,
                SnapshotAsync,
                cancellationToken)
            : ValueTask.FromResult(TerminalWaitOutcome.Unsupported());
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_failure is not null)
            {
                return ValueTask.FromResult(new PanelSessionSnapshot(
                    SessionLifecycle.Failed,
                    SessionHealth.Failed,
                    false,
                    _failure.Message,
                    _failure));
            }

            if (_closed)
            {
                return ValueTask.FromResult(new PanelSessionSnapshot(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    false,
                    "The terminal session is closed."));
            }

            if (_processExited)
            {
                return ValueTask.FromResult(new PanelSessionSnapshot(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    false,
                    DescribeProcessExitUnsafe()));
            }

            var hasActiveWork = NeedsCloseConfirmationUnsafe();
            var isMultiplexed = _launch.MultiplexerSession is not null;
            return ValueTask.FromResult(new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                hasActiveWork,
                // Said to whoever is about to close it, so it says what closing
                // would interrupt rather than which engine and renderer are
                // carrying it. The parts naming our own plumbing meant nothing
                // to the person reading them in a confirmation dialog.
                isMultiplexed
                    ? "protected by remote multiplexer"
                    : _rendererAttached
                    ? hasActiveWork
                        ? "still working"
                        : "waiting at a prompt"
                    : "running in the background"));
        }
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var sessionEvent in _events.Reader
                           .ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (sessionEvent.Sequence > afterSequence)
            {
                yield return sessionEvent;
            }
        }
    }

    public async IAsyncEnumerable<PanelNotificationEvent> WatchNotificationsAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var queued in _notifications.Reader
                           .ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            Interlocked.Add(
                ref _queuedNotificationUtf8Bytes,
                -queued.Utf8Bytes);
            var notification = queued.Notification;
            if (notification.Sequence > afterSequence)
            {
                yield return notification;
            }
        }
    }

    /// <summary>
    /// Queues a request to be noticed. Called from a native callback, so it
    /// only ever writes to a channel — anything that could block or throw
    /// belongs to the reader.
    /// </summary>
    internal void PublishNotification(PanelNotificationKind kind, string title, string body)
    {
        var notification = PanelNotificationTextBudget.Clamp(
            new PanelNotificationEvent(
                Interlocked.Increment(ref _notificationSequence),
                kind,
                title,
                body,
                DateTimeOffset.UtcNow));
        var utf8Bytes = PanelNotificationTextBudget.Measure(notification);
        if (!TryReserveNotificationBytes(utf8Bytes))
        {
            return;
        }

        if (!_notifications.Writer.TryWrite(
                new QueuedPanelNotification(notification, utf8Bytes)))
        {
            Interlocked.Add(ref _queuedNotificationUtf8Bytes, -utf8Bytes);
        }
    }

    internal long RetainedNotificationUtf8Bytes =>
        Volatile.Read(ref _queuedNotificationUtf8Bytes);

    private bool TryReserveNotificationBytes(int utf8Bytes)
    {
        while (true)
        {
            var current = Volatile.Read(ref _queuedNotificationUtf8Bytes);
            var next = current + utf8Bytes;
            if (next > MaximumQueuedNotificationUtf8Bytes)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _queuedNotificationUtf8Bytes,
                    next,
                    current) == current)
            {
                return true;
            }
        }
    }

    internal bool TryCaptureInteractiveStateNotification(
        string title,
        string body,
        DateTimeOffset observedAtUtc)
    {
        if (!TerminalInteractiveStateProtocol.IsProtocolNotification(title))
        {
            return false;
        }

        lock (_gate)
        {
            if (TerminalInteractiveStateProtocol.TryParse(
                    body,
                    _interactiveStateSequence,
                    observedAtUtc,
                    out var update))
            {
                _interactiveStateSequence = update.Sequence;
                _interactiveState = update.Snapshot;
            }
        }

        // Protocol messages are state transport, not user notifications. A
        // malformed one is consumed and ignored instead of becoming UI noise.
        return true;
    }

    private sealed record QueuedPanelNotification(
        PanelNotificationEvent Notification,
        int Utf8Bytes);

    public async ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_closed)
            {
                return PanelCloseOutcome.AlreadyClosed;
            }

            if (!_processExited
                && mode == PanelCloseMode.Graceful
                && NeedsCloseConfirmationUnsafe())
            {
                return PanelCloseOutcome.ConfirmationRequired;
            }
        }

        await StopAsync(force: mode == PanelCloseMode.Force).ConfigureAwait(false);
        return mode == PanelCloseMode.Force
            ? PanelCloseOutcome.ForceTerminated
            : PanelCloseOutcome.GracefullyClosed;
    }

    public async ValueTask DisposeAsync() =>
        await StopAsync(force: true).ConfigureAwait(false);

    private async ValueTask StopAsync(bool force)
    {
        Task? existingStop = null;
        lock (_gate)
        {
            if (_closed)
            {
                existingStop = _stopped.Task;
            }
            else
            {
                _closed = true;
                _rendererAttached = false;
            }
        }

        if (existingStop is not null)
        {
            await existingStop.ConfigureAwait(false);
            return;
        }

        ExceptionDispatchInfo? failure = null;

        void Capture(Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            if (force && !_processExited)
            {
                try
                {
                    _pty.Kill();
                }
                catch (InvalidOperationException)
                {
                    // The process can exit between the state check and the kill.
                }
                catch (Exception exception)
                {
                    Capture(exception);
                }
            }

            try
            {
                _lifetime.Cancel();
            }
            catch (Exception exception)
            {
                Capture(exception);
            }

            _writes.Writer.TryComplete(failure?.SourceException);
            try
            {
                _pty.ProcessExited -= OnProcessExited;
            }
            catch (Exception exception)
            {
                Capture(exception);
            }

            try
            {
                _pty.Dispose();
                using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _pty.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Capture(exception);
            }

            try
            {
                await Task.WhenAll(_readerTask, _writerTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Capture(exception);
            }

            try
            {
                DisposeNativeHandles();
            }
            catch (Exception exception)
            {
                Capture(exception);
            }

            try
            {
                if (_selfHandle.IsAllocated)
                {
                    _selfHandle.Free();
                }
            }
            catch (Exception exception)
            {
                Capture(exception);
            }

            try
            {
                _lifetime.Dispose();
            }
            catch (Exception exception)
            {
                Capture(exception);
            }

            lock (_gate)
            {
                PublishUnsafe(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    "Terminal session closed.");
                _events.Writer.TryComplete(failure?.SourceException);
                // A closed session has nothing left to ask for; a watcher
                // blocked on this channel would otherwise hang until its own
                // token fired.
                _notifications.Writer.TryComplete(failure?.SourceException);
            }
        }
        finally
        {
            if (failure is null)
            {
                _stopped.TrySetResult();
            }
            else
            {
                _stopped.TrySetException(failure.SourceException);
            }
        }

        failure?.Throw();
    }

    private void OnProcessExited(object? sender, PortablePtyExit eventArgs) =>
        MarkProcessExited(eventArgs.ExitCode);

    private bool TryMarkKnownProcessExit()
    {
        if (!_pty.TryGetExitCode(out var exitCode))
        {
            return false;
        }

        MarkProcessExited(exitCode);
        return true;
    }

    private void MarkProcessExited(int? exitCode)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _exitCode = exitCode ?? _exitCode;
            if (!_processExited)
            {
                _processExited = true;
                _writes.Writer.TryComplete();
            }

            PublishUnsafe(
                SessionLifecycle.Closed,
                SessionHealth.Ended,
                DescribeProcessExitUnsafe());
        }
    }

    private string DescribeProcessExitUnsafe() =>
        TerminalProcessExitDescription.Describe(_launch, _exitCode);

    private void Fail(string stableCode, Exception exception)
    {
        lock (_gate)
        {
            if (_closed || _failure is not null)
            {
                return;
            }

            _failure = new SessionFailure(stableCode, exception.Message, true);
            _writes.Writer.TryComplete(exception);
            PublishUnsafe(SessionLifecycle.Failed, SessionHealth.Failed, exception.Message);
        }
    }

    private void PublishUnsafe(SessionLifecycle lifecycle, SessionHealth health, string detail)
    {
        _sequence++;
        _events.Writer.TryWrite(new PanelSessionEvent(
            _sequence,
            lifecycle,
            health,
            DateTimeOffset.UtcNow,
            detail));
    }

    private void MarkContentChangedUnsafe()
    {
        _contentRevision++;
        _cachedRenderFrame = null;
    }

    private bool NeedsCloseConfirmationUnsafe()
    {
        // Closing the local SSH process only detaches an app-managed Screen
        // session. Its foreground command continues remotely, so it is not
        // work the close operation can interrupt and must not trigger the
        // destructive-work warning.
        if (_launch.MultiplexerSession is not null)
        {
            return false;
        }

        if (_shellActivity == TerminalShellActivityState.Idle)
        {
            return false;
        }

        if (!RemoteTerminalIdleClassifier.AppliesTo(_launch))
        {
            return true;
        }

        // Wrapped interactive sessions such as SSH and `docker exec` keep the
        // outer host command running while the inner shell is idle at its prompt.
        // For launches explicitly allowed to use prompt-shape fallback, classify
        // the visible inner shell instead of trusting the outer command state.
        // Anything ambiguous still requires confirmation.
        try
        {
            var snapshot = BuildScreenSnapshotUnsafe(BuildRenderFrameUnsafe());
            return !RemoteTerminalIdleClassifier.IsAtShellPrompt(snapshot);
        }
        catch
        {
            return true;
        }
    }

    private void DisposeNativeHandles()
    {
        ExceptionDispatchInfo? failure = null;

        void Dispose(IDisposable? resource)
        {
            if (resource is null)
            {
                return;
            }

            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        foreach (var marker in _semanticMarkers)
        {
            Dispose(marker.Reference);
        }

        _semanticMarkers.Clear();
        Dispose(_kittyVirtualPlacementIterator);
        Dispose(_kittyPlacementIterator);
        Dispose(_mouseEncoder);
        Dispose(_mouseEvent);
        Dispose(_keyEncoder);
        Dispose(_keyEvent);
        Dispose(_rowCells);
        Dispose(_rowIterator);
        Dispose(_renderState);
        Dispose(_terminal);
        failure?.Throw();
    }

    private static NativeHandles CreateNativeHandles(int columns, int rows)
    {
        GhosttyVtTerminalHandle? terminal = null;
        GhosttyVtRenderStateHandle? renderState = null;
        GhosttyVtRenderRowIteratorHandle? rowIterator = null;
        GhosttyVtRenderRowCellsHandle? rowCells = null;
        GhosttyVtKeyEventHandle? keyEvent = null;
        GhosttyVtKeyEncoderHandle? keyEncoder = null;
        GhosttyVtMouseEventHandle? mouseEvent = null;
        GhosttyVtMouseEncoderHandle? mouseEncoder = null;
        GhosttyVtKittyPlacementIteratorHandle? kittyPlacementIterator = null;
        GhosttyVtKittyVirtualPlacementIteratorHandle? kittyVirtualPlacementIterator = null;

        try
        {
            terminal = CreateTerminal(columns, rows);
            renderState = CreateRenderState();
            rowIterator = CreateRowIterator();
            rowCells = CreateRowCells();
            keyEvent = CreateKeyEvent();
            keyEncoder = CreateKeyEncoder();
            mouseEvent = CreateMouseEvent();
            mouseEncoder = CreateMouseEncoder();
            kittyPlacementIterator = CreateKittyPlacementIterator();
            kittyVirtualPlacementIterator = CreateKittyVirtualPlacementIterator();

            return new NativeHandles(
                terminal,
                renderState,
                rowIterator,
                rowCells,
                keyEvent,
                keyEncoder,
                mouseEvent,
                mouseEncoder,
                kittyPlacementIterator,
                kittyVirtualPlacementIterator);
        }
        catch
        {
            kittyVirtualPlacementIterator?.Dispose();
            kittyPlacementIterator?.Dispose();
            mouseEncoder?.Dispose();
            mouseEvent?.Dispose();
            keyEncoder?.Dispose();
            keyEvent?.Dispose();
            rowCells?.Dispose();
            rowIterator?.Dispose();
            renderState?.Dispose();
            terminal?.Dispose();
            throw;
        }
    }

    private static GhosttyVtTerminalHandle CreateTerminal(int columns, int rows)
    {
        EnsureSuccess(
            GhosttyVtNative.TerminalNew(
                0,
                out var handle,
                checked((ushort)columns),
                checked((ushort)rows)),
            "create terminal");
        return new GhosttyVtTerminalHandle(handle);
    }

    private static GhosttyVtRenderStateHandle CreateRenderState()
    {
        EnsureSuccess(GhosttyVtNative.RenderStateNew(0, out var handle), "create render state");
        return new GhosttyVtRenderStateHandle(handle);
    }

    private static GhosttyVtRenderRowIteratorHandle CreateRowIterator()
    {
        EnsureSuccess(GhosttyVtNative.RenderRowIteratorNew(0, out var handle), "create row iterator");
        return new GhosttyVtRenderRowIteratorHandle(handle);
    }

    private static GhosttyVtRenderRowCellsHandle CreateRowCells()
    {
        EnsureSuccess(GhosttyVtNative.RenderRowCellsNew(0, out var handle), "create cell iterator");
        return new GhosttyVtRenderRowCellsHandle(handle);
    }

    private static GhosttyVtKeyEventHandle CreateKeyEvent()
    {
        EnsureSuccess(GhosttyVtNative.KeyEventNew(0, out var handle), "create key event");
        return new GhosttyVtKeyEventHandle(handle);
    }

    private static GhosttyVtKeyEncoderHandle CreateKeyEncoder()
    {
        EnsureSuccess(GhosttyVtNative.KeyEncoderNew(0, out var handle), "create key encoder");
        return new GhosttyVtKeyEncoderHandle(handle);
    }

    private static GhosttyVtMouseEventHandle CreateMouseEvent()
    {
        EnsureSuccess(GhosttyVtNative.MouseEventNew(0, out var handle), "create mouse event");
        return new GhosttyVtMouseEventHandle(handle);
    }

    private static GhosttyVtMouseEncoderHandle CreateMouseEncoder()
    {
        EnsureSuccess(GhosttyVtNative.MouseEncoderNew(0, out var handle), "create mouse encoder");
        return new GhosttyVtMouseEncoderHandle(handle);
    }

    private static GhosttyVtKittyPlacementIteratorHandle CreateKittyPlacementIterator()
    {
        EnsureSuccess(
            GhosttyVtNative.KittyPlacementIteratorNew(0, out var handle),
            "create Kitty placement iterator");
        return new GhosttyVtKittyPlacementIteratorHandle(handle);
    }

    private static GhosttyVtKittyVirtualPlacementIteratorHandle
        CreateKittyVirtualPlacementIterator()
    {
        EnsureSuccess(
            GhosttyVtNative.KittyVirtualPlacementIteratorNew(0, out var handle),
            "create Kitty virtual-placement iterator");
        return new GhosttyVtKittyVirtualPlacementIteratorHandle(handle);
    }

    private static void EnsureSuccess(GhosttyVtResult result, string operation)
    {
        if (result == GhosttyVtResult.Success)
        {
            return;
        }

        throw new InvalidOperationException(
            $"libghostty-vt could not {operation} ({result}).");
    }

    private static TerminalRenderProfileSnapshot CreateFallbackProfile() =>
        new(
            13,
            TerminalCursorStyle.Block,
            cursorBlink: false,
            scrollbackLines: 10_000,
            TerminalPalette.AsuraDark);

    private sealed record NativeHandles(
        GhosttyVtTerminalHandle Terminal,
        GhosttyVtRenderStateHandle RenderState,
        GhosttyVtRenderRowIteratorHandle RowIterator,
        GhosttyVtRenderRowCellsHandle RowCells,
        GhosttyVtKeyEventHandle KeyEvent,
        GhosttyVtKeyEncoderHandle KeyEncoder,
        GhosttyVtMouseEventHandle MouseEvent,
        GhosttyVtMouseEncoderHandle MouseEncoder,
        GhosttyVtKittyPlacementIteratorHandle KittyPlacementIterator,
        GhosttyVtKittyVirtualPlacementIteratorHandle KittyVirtualPlacementIterator);

    private sealed class QueuedTerminalInput
    {
        private readonly TaskCompletionSource<TerminalRevisionBoundMouseOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal QueuedTerminalInput(
            byte[] bytes,
            CancellationToken cancellationToken,
            bool protocolResponse = false)
        {
            Bytes = bytes;
            CancellationToken = cancellationToken;
            ProtocolResponse = protocolResponse;
        }

        internal QueuedTerminalInput(
            TerminalMouseInput mouseInput,
            long expectedContentRevision,
            CancellationToken cancellationToken)
        {
            RevisionBoundMouseInput = mouseInput;
            ExpectedContentRevision = expectedContentRevision;
            Bytes = [];
            CancellationToken = cancellationToken;
        }

        internal byte[] Bytes { get; }

        internal CancellationToken CancellationToken { get; }

        internal bool ProtocolResponse { get; }

        internal TerminalMouseInput? RevisionBoundMouseInput { get; }

        internal long ExpectedContentRevision { get; }

        internal Task<TerminalRevisionBoundMouseOutcome> Completion => _completion.Task;

        internal void Complete() => Complete(TerminalRevisionBoundMouseOutcome.Sent);

        internal void Complete(TerminalRevisionBoundMouseOutcome outcome) =>
            _completion.TrySetResult(outcome);

        internal void Cancel() => Cancel(CancellationToken);

        internal void Cancel(CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                _completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                _completion.TrySetCanceled(CancellationToken.None);
            }
        }

        internal void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct SelectionAnchor(int Column, int Row);

    private sealed record SemanticMarker(
        TerminalShellIntegrationEvent Event,
        GhosttyVtTerminalScreen Screen,
        GhosttyVtTrackedGridRefHandle? Reference);

    private enum TerminalShellActivityState
    {
        Unknown,
        Idle,
        Running,
    }
}
