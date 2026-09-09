using System.Diagnostics;
using Asura.Application;
using Asura.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Controls;

/// <summary>
/// Owns the desktop attachment to a portable terminal session while delegating all
/// emulation and PTY state to the session host. Screen polling is single-flight and
/// bounded; only application DTOs cross into the Avalonia presentation project.
/// </summary>
public sealed class ManagedTerminalSessionHost : ContentControl, IManagedTerminalInputSink
{
    private const int StatePollInterval = 10;
    private static readonly IBrush StartingBrush = Brush.Parse("#8B8B91");
    private static readonly IBrush LiveBrush = Brush.Parse("#3FB950");
    private static readonly IBrush UnavailableBrush = Brush.Parse("#FF7A55");
    private static readonly IBrush ExitedBrush = Brush.Parse("#FFB224");
    private static readonly TimeSpan HumanInputLeaseDuration =
        TimeSpan.FromHours(8);

    public static readonly StyledProperty<ISessionHostClient?> SessionClientProperty =
        AvaloniaProperty.Register<ManagedTerminalSessionHost, ISessionHostClient?>(
            nameof(SessionClient));

    /// <summary>
    /// The live render profile, applied without restarting the session.
    /// </summary>
    public static readonly StyledProperty<TerminalRenderProfileSnapshot?> RenderProfileProperty =
        AvaloniaProperty.Register<ManagedTerminalSessionHost, TerminalRenderProfileSnapshot?>(
            nameof(RenderProfile));

    public static readonly StyledProperty<double> BackgroundOpacityProperty =
        AvaloniaProperty.Register<ManagedTerminalSessionHost, double>(
            nameof(BackgroundOpacity),
            1);

    public static readonly StyledProperty<EnsureTerminalSessionRequest?> SessionRequestProperty =
        AvaloniaProperty.Register<ManagedTerminalSessionHost, EnsureTerminalSessionRequest?>(
            nameof(SessionRequest));

    public static readonly StyledProperty<ClientId?> ClientIdProperty =
        AvaloniaProperty.Register<ManagedTerminalSessionHost, ClientId?>(nameof(ClientId));

    public static readonly StyledProperty<TerminalStartupCommandDispatcher?> StartupCommandDispatcherProperty =
        AvaloniaProperty.Register<ManagedTerminalSessionHost, TerminalStartupCommandDispatcher?>(
            nameof(StartupCommandDispatcher));

    public static readonly StyledProperty<TerminalStartupCommandDispatchState?>
        StartupCommandDispatchStateProperty =
            AvaloniaProperty.Register<
                ManagedTerminalSessionHost,
                TerminalStartupCommandDispatchState?>(
                nameof(StartupCommandDispatchState));

    public static readonly DirectProperty<ManagedTerminalSessionHost, bool> IsLiveProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSessionHost, bool>(
            nameof(IsLive),
            control => control.IsLive);

    public static readonly DirectProperty<ManagedTerminalSessionHost, string?> InitializationErrorProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSessionHost, string?>(
            nameof(InitializationError),
            control => control.InitializationError);

    public static readonly DirectProperty<ManagedTerminalSessionHost, string> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSessionHost, string>(
            nameof(StatusText),
            control => control.StatusText);

    public static readonly DirectProperty<ManagedTerminalSessionHost, IBrush> StatusBrushProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSessionHost, IBrush>(
            nameof(StatusBrush),
            control => control.StatusBrush);

    public static readonly DirectProperty<ManagedTerminalSessionHost, bool> ShowFallbackProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSessionHost, bool>(
            nameof(ShowFallback),
            control => control.ShowFallback);

    public static readonly DirectProperty<ManagedTerminalSessionHost, string> StatusMessageProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSessionHost, string>(
            nameof(StatusMessage),
            control => control.StatusMessage);

    private readonly ManagedTerminalSurface _surface;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _resizeTimer;
    private readonly SemaphoreSlim _screenReadGate = new(1, 1);
    private readonly SemaphoreSlim _resizeGate = new(1, 1);
    private readonly SemaphoreSlim _physicalInputGate = new(1, 1);
    private readonly object _resizeQueueGate = new();
    private CancellationTokenSource? _attachmentLifetime;
    private ISessionHostClient? _attachedClient;
    private ClientId? _attachedClientId;
    private SessionId? _attachedSessionId;
    private AttachmentId? _attachmentId;
    private InputLeaseId? _inputLeaseId;
    private ViewportDescriptor? _lastViewport;
    private PendingTerminalResize? _pendingResize;
    private long _initializationGeneration;
    private int _pollTicks;
    private bool _pollInProgress;
    private bool? _renderFramesSupported;
    private bool _isAttachedToVisualTree;
    private bool _focusRequested;
    private SessionLifecycle? _lastNotifiedLifecycle;
    private SessionHealth? _lastNotifiedHealth;
    private string? _lastNotifiedFailureCode;
    private bool _isLive;
    private string? _initializationError;
    private string _statusText = "Starting";
    private IBrush _statusBrush = StartingBrush;
    private bool _showFallback;
    private string _statusMessage = string.Empty;

    public ManagedTerminalSessionHost()
    {
        Focusable = true;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        _surface = new ManagedTerminalSurface
        {
            InputSink = this,
        };
        _surface.ViewportChanged += OnSurfaceViewportChanged;
        _surface.GotFocus += OnSurfaceGotFocus;
        _surface.LostFocus += OnSurfaceLostFocus;
        _surface.FocusRequested += OnSurfaceFocusRequested;
        _surface.SetInputReady(false);
        Content = _surface;
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _pollTimer.Tick += OnPollTimerTick;
        _resizeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _resizeTimer.Tick += OnResizeTimerTick;
    }

    public event EventHandler<TerminalSessionSnapshotEventArgs>? SessionSnapshotChanged;

    public event EventHandler<TerminalSessionFailureEventArgs>? SessionInitializationFailed;

    public event EventHandler<TerminalStartupCommandDispatchEventArgs>?
        StartupCommandDispatchCompleted;

    public ISessionHostClient? SessionClient
    {
        get => GetValue(SessionClientProperty);
        set => SetValue(SessionClientProperty, value);
    }

    public TerminalRenderProfileSnapshot? RenderProfile
    {
        get => GetValue(RenderProfileProperty);
        set => SetValue(RenderProfileProperty, value);
    }

    public double BackgroundOpacity
    {
        get => GetValue(BackgroundOpacityProperty);
        set => SetValue(BackgroundOpacityProperty, value);
    }

    public EnsureTerminalSessionRequest? SessionRequest
    {
        get => GetValue(SessionRequestProperty);
        set => SetValue(SessionRequestProperty, value);
    }

    public ClientId? ClientId
    {
        get => GetValue(ClientIdProperty);
        set => SetValue(ClientIdProperty, value);
    }

    public TerminalStartupCommandDispatcher? StartupCommandDispatcher
    {
        get => GetValue(StartupCommandDispatcherProperty);
        set => SetValue(StartupCommandDispatcherProperty, value);
    }

    public TerminalStartupCommandDispatchState? StartupCommandDispatchState
    {
        get => GetValue(StartupCommandDispatchStateProperty);
        set => SetValue(StartupCommandDispatchStateProperty, value);
    }

    public bool IsLive
    {
        get => _isLive;
        private set => SetAndRaise(IsLiveProperty, ref _isLive, value);
    }

    public string? InitializationError
    {
        get => _initializationError;
        private set => SetAndRaise(InitializationErrorProperty, ref _initializationError, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetAndRaise(StatusTextProperty, ref _statusText, value);
    }

    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetAndRaise(StatusBrushProperty, ref _statusBrush, value);
    }

    public bool ShowFallback
    {
        get => _showFallback;
        private set => SetAndRaise(ShowFallbackProperty, ref _showFallback, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetAndRaise(StatusMessageProperty, ref _statusMessage, value);
    }

    internal ManagedTerminalSurface Surface => _surface;

    internal ViewportDescriptor? LastViewport => _lastViewport;

    internal Task InitializeForTestingAsync(CancellationToken cancellationToken = default)
    {
        _attachmentLifetime?.Cancel();
        _attachmentLifetime?.Dispose();
        _attachmentLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _surface.SetInputReady(false);
        var generation = ++_initializationGeneration;
        return InitializeSessionAsync(generation, _attachmentLifetime.Token);
    }

    internal Task ResizeForTestingAsync() => ResizeTerminalAsync();

    internal Task PollForTestingAsync() => PollTerminalAsync();

    internal void StopForTesting() => StopSession();

    internal bool RequestInputFocus()
    {
        _focusRequested = true;
        if (!_surface.IsInputReady)
        {
            return IsFocused || Focus();
        }

        if (_surface.IsFocused)
        {
            // Pointer presses and owner-window reactivation do not necessarily raise
            // GotFocus when Avalonia retained logical focus across a modal window.
            // Renew the host-side focus contract in that case as well.
            _ = FocusTerminalAsync();
            return true;
        }

        return _surface.Focus();
    }

    public ValueTask SendTextAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        ((IManagedTerminalInputSink)this).SendTextAsync(text, cancellationToken);

    public async ValueTask<string> ReadScreenAsync(CancellationToken cancellationToken = default)
    {
        await _screenReadGate.WaitAsync(cancellationToken);
        try
        {
            var result = await RequireClient().ReadTerminalScreenAsync(
                RequireSessionRequest().SessionId,
                NewContext(),
                cancellationToken);
            return RequireSuccess(result).PlainText;
        }
        finally
        {
            _screenReadGate.Release();
        }
    }

    public ValueTask<TerminalPasteResult> PasteTextAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        _surface.PasteTextAsync(text, cancellationToken);

    public bool TryCancelPendingInteraction() => _surface.CancelPendingInteraction();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        RestartSession();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttachedToVisualTree = false;
        StopSession();
        SetState(TerminalHostState.Stopped);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (ReferenceEquals(e.Source, this))
        {
            RequestInputFocus();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RenderProfileProperty)
        {
            // Typography only. Restarting the session would throw away scrollback
            // for a font change.
            _surface.Profile = RenderProfile ?? SessionRequest?.Launch.RenderProfile;
        }
        else if (change.Property == BackgroundOpacityProperty)
        {
            _surface.BackgroundOpacity = BackgroundOpacity;
        }
        else if (change.Property == SessionClientProperty
            || change.Property == SessionRequestProperty
            || change.Property == ClientIdProperty)
        {
            _surface.Profile = RenderProfile ?? SessionRequest?.Launch.RenderProfile;
            _surface.Keymap = SessionRequest?.Launch.Keymap;
            if (_isAttachedToVisualTree)
            {
                RestartSession();
            }
        }
        else if (change.Property == StartupCommandDispatchStateProperty)
        {
            // The state validates the current session owner before mutation, so a state binding
            // that arrives last may safely trigger without combining different panel instances.
            if (IsLive && _attachmentLifetime is { } lifetime)
            {
                _ = SendStartupCommandsIfNeededAsync(
                    _initializationGeneration,
                    lifetime.Token);
            }
        }
        else if (change.Property == StartupCommandDispatcherProperty
            && IsLive
            && _attachmentLifetime is { } lifetime)
        {
            _ = SendStartupCommandsIfNeededAsync(_initializationGeneration, lifetime.Token);
        }
        else if (change.Property == IsKeyboardFocusWithinProperty
            && !IsKeyboardFocusWithin)
        {
            _focusRequested = false;
        }
    }

    async ValueTask IManagedTerminalInputSink.SendTextAsync(
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        _ = await DeliverHumanPtyInputAsync(
            (authority, token) => authority.Client.WriteTerminalAsync(
                new TerminalWriteRequest(
                    authority.SessionId,
                    authority.LeaseId,
                    text),
                authority.Context,
                token),
            cancellationToken);
    }

    async ValueTask IManagedTerminalInputSink.SendKeyAsync(
        TerminalKeyStroke keyStroke,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyStroke);
        _ = await DeliverHumanPtyInputAsync(
            (authority, token) => authority.Client.SendTerminalKeyAsync(
                new TerminalKeyRequest(
                    authority.SessionId,
                    authority.LeaseId,
                    keyStroke),
                authority.Context,
                token),
            cancellationToken);
    }

    async ValueTask IManagedTerminalInputSink.SendPhysicalKeyAsync(
        TerminalPhysicalKeyEvent keyEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyEvent);
        _ = await DeliverHumanPtyInputAsync(
            (authority, token) => authority.Client.SendTerminalPhysicalKeyAsync(
                new TerminalPhysicalKeyRequest(
                    authority.SessionId,
                    authority.LeaseId,
                    keyEvent),
                authority.Context,
                token),
            cancellationToken);
    }

    async ValueTask IManagedTerminalInputSink.SendMouseAsync(
        TerminalMouseInput mouseInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mouseInput);
        _ = await DeliverHumanPtyInputAsync(
            (authority, token) => authority.Client.SendTerminalMouseAsync(
                new TerminalMouseRequest(
                    authority.SessionId,
                    authority.LeaseId,
                    mouseInput),
                authority.Context,
                token),
            cancellationToken);
    }

    async ValueTask IManagedTerminalInputSink.ScrollViewportAsync(
        TerminalViewportScrollInput scrollInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollInput);
        var result = await RequireClient().ScrollTerminalViewportAsync(
            new TerminalViewportScrollRequest(
                RequireSessionRequest().SessionId,
                RequireInputLease(),
                scrollInput),
            NewContext(),
            cancellationToken);
        _ = RequireSuccess(result);
    }

    async ValueTask<bool> IManagedTerminalInputSink.ClearScrollbackAsync(
        CancellationToken cancellationToken)
    {
        var result = await RequireClient().ClearTerminalScrollbackAsync(
            new TerminalClearScrollbackRequest(
                RequireSessionRequest().SessionId,
                RequireInputLease()),
            NewContext(),
            cancellationToken);
        if (result is HostResult<Unit>.Failure
            {
                Error.Code: HostErrorCode.CapabilityNotSupported,
            })
        {
            return false;
        }

        _ = RequireSuccess(result);
        await ReadAndApplyScreenAsync(_initializationGeneration, cancellationToken);
        return true;
    }

    async ValueTask<TerminalFindResult?> IManagedTerminalInputSink.FindAsync(
        TerminalFindInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await RequireClient().FindTerminalAsync(
            new TerminalFindRequest(
                RequireSessionRequest().SessionId,
                RequireInputLease(),
                input),
            NewContext(),
            cancellationToken);
        if (result is HostResult<TerminalFindResult>.Failure
            {
                Error.Code: HostErrorCode.CapabilityNotSupported,
            })
        {
            return null;
        }

        var find = RequireSuccess(result);
        await ReadAndApplyScreenAsync(_initializationGeneration, cancellationToken);
        return find;
    }

    async ValueTask IManagedTerminalInputSink.UpdateSelectionAsync(
        TerminalSelectionInput selectionInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectionInput);
        var result = await RequireClient().UpdateTerminalSelectionAsync(
            new TerminalSelectionRequest(
                RequireSessionRequest().SessionId,
                RequireInputLease(),
                selectionInput),
            NewContext(),
            cancellationToken);
        _ = RequireSuccess(result);
    }

    async ValueTask<TerminalSelectionText> IManagedTerminalInputSink.ReadSelectionAsync(
        CancellationToken cancellationToken)
    {
        var result = await RequireClient().ReadTerminalSelectionAsync(
            new TerminalSelectionReadRequest(
                RequireSessionRequest().SessionId,
                RequireInputLease()),
            NewContext(),
            cancellationToken);
        return RequireSuccess(result);
    }

    async ValueTask<TerminalPasteResult> IManagedTerminalInputSink.PasteAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pasteInput);
        return await DeliverHumanPtyInputAsync(
            (authority, token) => authority.Client.PasteTerminalAsync(
                new TerminalPasteRequest(
                    authority.SessionId,
                    authority.LeaseId,
                    pasteInput),
                authority.Context,
                token),
            cancellationToken);
    }

    /// <summary>
    /// Reclaims the exact managed attachment for a physical human PTY input and
    /// keeps that acquisition adjacent to its dispatch. Passive renderer work
    /// deliberately does not pass through this boundary.
    /// </summary>
    private async ValueTask<T> DeliverHumanPtyInputAsync<T>(
        Func<
            HumanPtyInputAuthority,
            CancellationToken,
            ValueTask<HostResult<T>>> deliver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        await _physicalInputGate.WaitAsync(cancellationToken);
        try
        {
            var client = _attachedClient
                ?? throw new InvalidOperationException(
                    "The attached session-host client is unavailable.");
            var clientId = _attachedClientId
                ?? throw new InvalidOperationException(
                    "The attached desktop client identity is unavailable.");
            var sessionId = _attachedSessionId
                ?? throw new InvalidOperationException(
                    "The attached terminal session is unavailable.");
            var attachmentId = _attachmentId
                ?? throw new InvalidOperationException(
                    "The managed terminal attachment is unavailable.");
            var lifetime = _attachmentLifetime
                ?? throw new InvalidOperationException(
                    "The managed terminal attachment has stopped.");
            var generation = _initializationGeneration;
            using var linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.Token);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            var context = OperationContext.ForHuman(clientId);
            var decision = RequireSuccess(await client.AcquireInputLeaseAsync(
                new AcquireInputLeaseRequest(
                    sessionId,
                    attachmentId,
                    HumanInputLeaseDuration),
                context,
                linkedCancellation.Token));
            if (!decision.Granted || decision.Lease is null)
            {
                throw new InvalidOperationException(decision.Detail);
            }

            var lease = decision.Lease;
            if (lease.SessionId != sessionId
                || lease.AttachmentId != attachmentId
                || lease.Holder.Kind != ActorKind.Human
                || lease.Holder.ClientId != clientId
                || !string.Equals(lease.Holder.Id.Value, clientId.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The session host returned an input lease for a different attachment or client.");
            }

            if (generation != _initializationGeneration
                || !ReferenceEquals(client, _attachedClient)
                || clientId != _attachedClientId
                || sessionId != _attachedSessionId
                || attachmentId != _attachmentId
                || !ReferenceEquals(lifetime, _attachmentLifetime))
            {
                throw new OperationCanceledException(
                    "The managed terminal attachment changed before input dispatch.",
                    linkedCancellation.Token);
            }

            _inputLeaseId = lease.Id;
            return RequireSuccess(await deliver(
                new HumanPtyInputAuthority(
                    client,
                    sessionId,
                    lease.Id,
                    context),
                linkedCancellation.Token));
        }
        finally
        {
            _physicalInputGate.Release();
        }
    }

    private void RestartSession()
    {
        StopSession();
        _lastNotifiedLifecycle = null;
        _lastNotifiedHealth = null;
        _lastNotifiedFailureCode = null;
        _lastViewport = null;
        if (SessionClient is null || SessionRequest is null || ClientId is null)
        {
            SetState(TerminalHostState.Waiting);
            return;
        }

        var generation = ++_initializationGeneration;
        _attachmentLifetime = new CancellationTokenSource();
        _ = InitializeSessionAsync(generation, _attachmentLifetime.Token);
    }

    private void StopSession()
    {
        _initializationGeneration++;
        _pollTimer.Stop();
        _resizeTimer.Stop();
        _pollTicks = 0;
        lock (_resizeQueueGate)
        {
            _pendingResize = null;
        }

        _attachmentLifetime?.Cancel();
        _attachmentLifetime?.Dispose();
        _attachmentLifetime = null;
        _renderFramesSupported = null;
        _surface.SetInputReady(false);
        _surface.ClearRenderFrame();
        DetachRenderer();
    }

    private async Task InitializeSessionAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        ISessionHostClient? pendingClient = null;
        ClientId? pendingClientId = null;
        SessionId? pendingSessionId = null;
        AttachmentId? pendingAttachmentId = null;
        try
        {
            var client = RequireClient();
            var request = RequireSessionRequest();
            var clientId = ClientId
                ?? throw new InvalidOperationException("The desktop client identity is unavailable.");
            var context = OperationContext.ForHuman(clientId);
            _ = RequireSuccess(await client.EnsureTerminalSessionAsync(
                request,
                context,
                cancellationToken));

            var attachment = await AttachInteractiveAsync(
                client,
                request,
                clientId,
                cancellationToken);
            pendingClient = client;
            pendingClientId = clientId;
            pendingSessionId = request.SessionId;
            pendingAttachmentId = attachment.Attachment.Id;
            if (generation != _initializationGeneration)
            {
                return;
            }

            var viewport = CurrentViewport();
            _ = RequireSuccess(await client.AttachTerminalRendererAsync(
                new AttachTerminalRendererRequest(
                    request.SessionId,
                    attachment.Attachment.Id,
                    new NativeRendererHost("Asura.Managed", 0, viewport)),
                context,
                cancellationToken));

            var lease = RequireSuccess(await client.AcquireInputLeaseAsync(
                new AcquireInputLeaseRequest(
                    request.SessionId,
                    attachment.Attachment.Id,
                    HumanInputLeaseDuration),
                context,
                cancellationToken));
            if (!lease.Granted || lease.Lease is null)
            {
                throw new InvalidOperationException(lease.Detail);
            }

            var snapshot = RequireSuccess(await client.GetSnapshotAsync(
                request.SessionId,
                context,
                cancellationToken));
            TerminalScreenSnapshot screen;
            await _screenReadGate.WaitAsync(cancellationToken);
            try
            {
                screen = RequireSuccess(await client.ReadTerminalScreenAsync(
                    request.SessionId,
                    context,
                    cancellationToken));
            }
            finally
            {
                _screenReadGate.Release();
            }

            var renderFrame = await TryReadRenderFrameAsync(
                client,
                request.SessionId,
                context,
                cancellationToken);

            if (generation != _initializationGeneration)
            {
                return;
            }

            _attachedClient = client;
            _attachedClientId = clientId;
            _attachedSessionId = request.SessionId;
            _attachmentId = attachment.Attachment.Id;
            _inputLeaseId = lease.Lease.Id;
            _lastViewport = viewport;
            pendingAttachmentId = null;
            ApplySnapshot(snapshot);
            _surface.UpdateSnapshot(screen);
            if (renderFrame is not null)
            {
                _surface.UpdateRenderFrame(renderFrame);
            }

            _surface.SetInputReady(true);
            _pollTimer.Start();
            RestoreRequestedFocus();
            // The viewport above was measured before the layout pass that gives
            // this host its bounds — attaching happens on the way into the visual
            // tree, arranging happens after. Whatever the surface reports now is
            // what the terminal must actually be, and this is the only moment
            // that knows both: a viewport change raised while attaching had no
            // attachment to resize, and once the layout settles nothing raises
            // another. Without this a workspace brought back from the background
            // stayed at the 80x24 fallback its panel was attached with.
            await ResizeTerminalAsync();
            await SendStartupCommandsIfNeededAsync(generation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == _initializationGeneration)
            {
                SetState(TerminalHostState.Stopped);
            }
        }
        catch (Exception exception)
        {
            if (generation == _initializationGeneration)
            {
                _surface.SetInputReady(false);
                DetachRenderer();
                SetState(TerminalHostState.Error, exception.Message);
                SessionInitializationFailed?.Invoke(
                    this,
                    new TerminalSessionFailureEventArgs(new SessionFailure(
                        "terminal_managed_renderer_initialization_failed",
                        "The managed terminal renderer could not be initialized.",
                        Retryable: true)));
            }

            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "terminal.renderer-attachment.failed",
                exception);
        }
        finally
        {
            if (pendingClient is not null
                && pendingClientId is { } staleClientId
                && pendingSessionId is { } staleSessionId
                && pendingAttachmentId is { } staleAttachmentId)
            {
                await DetachStaleAttachmentAsync(
                    pendingClient,
                    staleClientId,
                    staleSessionId,
                    staleAttachmentId);
            }
        }
    }

    private async Task<AttachmentResult> AttachInteractiveAsync(
        ISessionHostClient client,
        EnsureTerminalSessionRequest request,
        ClientId clientId,
        CancellationToken cancellationToken)
    {
        HostResult<AttachmentResult>? lastResult = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            lastResult = await client.AttachAsync(
                new AttachSessionRequest(
                    request.SessionId,
                    clientId,
                    AttachmentKind.Interactive,
                    CurrentViewport(),
                    InteractiveCapabilities()),
                    OperationContext.ForHuman(clientId),
                    cancellationToken);
            if (lastResult is HostResult<AttachmentResult>.Success success)
            {
                return success.Value;
            }

            if (lastResult is not HostResult<AttachmentResult>.Failure
                {
                    Error.Code: HostErrorCode.CapabilityNotSupported,
                })
            {
                return RequireSuccess(lastResult);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
        }

        return RequireSuccess(lastResult
            ?? throw new InvalidOperationException("The managed terminal attachment did not start."));
    }

    private async void OnPollTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await PollTerminalAsync();
    }

    private async Task PollTerminalAsync()
    {
        if (_pollInProgress
            || SessionClient is null
            || SessionRequest is null
            || _attachmentLifetime is not { } lifetime)
        {
            return;
        }

        _pollInProgress = true;
        var generation = _initializationGeneration;
        try
        {
            await ReadAndApplyTerminalStateAsync(generation, lifetime.Token);
            if (generation != _initializationGeneration)
            {
                return;
            }

            _pollTicks++;
            if (_pollTicks % StatePollInterval == 0)
            {
                if (_renderFramesSupported == true)
                {
                    // The rich frame deliberately excludes interaction metadata
                    // such as alternate-screen and mouse-reporting modes. Keep a
                    // low-frequency bounded snapshot for input routing only.
                    await ReadAndApplyScreenAsync(generation, lifetime.Token);
                }

                var snapshot = RequirePollingSuccess(await SessionClient.GetSnapshotAsync(
                    SessionRequest.SessionId,
                    NewContext(),
                    lifetime.Token));
                if (generation != _initializationGeneration)
                {
                    return;
                }

                ApplySnapshot(snapshot);
                await SendStartupCommandsIfNeededAsync(
                    _initializationGeneration,
                    lifetime.Token);
                if (snapshot.Descriptor.Lifecycle
                    is SessionLifecycle.Closed or SessionLifecycle.Failed)
                {
                    _pollTimer.Stop();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            _pollTimer.Stop();
        }
        catch (HostOperationException exception)
        {
            if (generation != _initializationGeneration
                || lifetime.IsCancellationRequested
                || await TryStopForClosedSessionAsync(
                    exception.Error,
                    generation,
                    lifetime.Token))
            {
                _pollTimer.Stop();
                return;
            }

            _pollTimer.Stop();
            SetState(TerminalHostState.Error, exception.Message);
            SecretSafeDiagnostics.WriteTrace("terminal.poll.failed", exception);
        }
        catch (Exception exception)
        {
            _pollTimer.Stop();
            if (generation != _initializationGeneration
                || lifetime.IsCancellationRequested)
            {
                return;
            }

            SetState(TerminalHostState.Error, exception.Message);
            SecretSafeDiagnostics.WriteTrace("terminal.poll.failed", exception);
        }
        finally
        {
            _pollInProgress = false;
        }
    }

    private async Task<bool> TryStopForClosedSessionAsync(
        HostError readError,
        long generation,
        CancellationToken cancellationToken)
    {
        if (readError.Code is not (HostErrorCode.SessionClosed or HostErrorCode.EngineFailed)
            || generation != _initializationGeneration
            || SessionClient is not { } client
            || SessionRequest is not { } request)
        {
            return false;
        }

        try
        {
            var result = await client.GetSnapshotAsync(
                request.SessionId,
                NewContext(),
                cancellationToken);
            if (generation != _initializationGeneration
                || result is not HostResult<SessionSnapshot>.Success
                {
                    Value.Descriptor.Lifecycle: SessionLifecycle.Closed,
                } success)
            {
                return false;
            }

            _surface.SetInputReady(false);
            ApplySnapshot(success.Value);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace(
                "terminal.poll-close-confirmation.failed",
                exception);
            return false;
        }
    }

    private async Task ReadAndApplyScreenAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        await _screenReadGate.WaitAsync(cancellationToken);
        try
        {
            if (generation != _initializationGeneration)
            {
                return;
            }

            var result = await RequireClient().ReadTerminalScreenAsync(
                RequireSessionRequest().SessionId,
                NewContext(),
                cancellationToken);
            if (generation == _initializationGeneration)
            {
                _surface.UpdateSnapshot(RequirePollingSuccess(result));
            }
        }
        finally
        {
            _screenReadGate.Release();
        }
    }

    private async Task ReadAndApplyTerminalStateAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        if (_renderFramesSupported != false)
        {
            var client = RequireClient();
            var frame = await TryReadRenderFrameAsync(
                client,
                RequireSessionRequest().SessionId,
                NewContext(),
                cancellationToken);
            if (generation != _initializationGeneration)
            {
                return;
            }

            if (frame is not null)
            {
                _surface.UpdateRenderFrame(frame);
                return;
            }
        }

        await ReadAndApplyScreenAsync(generation, cancellationToken);
    }

    private async ValueTask<TerminalRenderFrame?> TryReadRenderFrameAsync(
        ISessionHostClient client,
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var result = await client.ReadTerminalRenderFrameAsync(
            sessionId,
            context,
            cancellationToken);
        if (result is HostResult<TerminalRenderFrame>.Success success)
        {
            _renderFramesSupported = true;
            return success.Value;
        }

        if (result is HostResult<TerminalRenderFrame>.Failure
            {
                Error.Code: HostErrorCode.CapabilityNotSupported,
            })
        {
            _renderFramesSupported = false;
            return null;
        }

        _ = RequirePollingSuccess(result);
        throw new UnreachableException();
    }

    private async Task FocusTerminalAsync()
    {
        if (!_surface.IsInputReady
            || _inputLeaseId is null
            || SessionClient is null
            || SessionRequest is null)
        {
            return;
        }

        try
        {
            _ = RequireSuccess(await SessionClient.FocusTerminalAsync(
                SessionRequest.SessionId,
                NewContext(),
                _attachmentLifetime?.Token ?? CancellationToken.None));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace("terminal.focus.failed", exception);
        }
    }

    private async Task BlurTerminalAsync()
    {
        if (_inputLeaseId is null
            || SessionClient is null
            || SessionRequest is null)
        {
            return;
        }

        try
        {
            _ = RequireSuccess(await SessionClient.BlurTerminalAsync(
                SessionRequest.SessionId,
                NewContext(),
                _attachmentLifetime?.Token ?? CancellationToken.None));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace("terminal.blur.failed", exception);
        }
    }

    private async Task ResizeTerminalAsync()
    {
        if (_attachmentId is not { } attachmentId
            || _attachedClient is not { } client
            || _attachedSessionId is not { } sessionId
            || _attachedClientId is not { } clientId
            || Bounds.Width <= 0
            || Bounds.Height <= 0)
        {
            return;
        }

        var generation = _initializationGeneration;
        var viewport = CurrentViewport();
        var context = OperationContext.ForHuman(clientId);
        var cancellationToken = _attachmentLifetime?.Token ?? CancellationToken.None;
        lock (_resizeQueueGate)
        {
            _pendingResize = new PendingTerminalResize(
                generation,
                client,
                sessionId,
                attachmentId,
                viewport,
                context,
                cancellationToken);
        }

        await _resizeGate.WaitAsync();
        try
        {
            PendingTerminalResize? resize;
            lock (_resizeQueueGate)
            {
                resize = _pendingResize;
                _pendingResize = null;
            }

            if (resize is null
                || resize.Generation != _initializationGeneration
                || _attachmentId != resize.AttachmentId)
            {
                return;
            }

            if (resize.Viewport == _lastViewport)
            {
                return;
            }

            _ = RequireSuccess(await resize.Client.ResizeTerminalAsync(
                new TerminalResizeRequest(
                    resize.SessionId,
                    resize.AttachmentId,
                    resize.Viewport),
                resize.Context,
                resize.CancellationToken));

            if (resize.Generation == _initializationGeneration
                && _attachmentId == resize.AttachmentId)
            {
                _lastViewport = resize.Viewport;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace("terminal.resize.failed", exception);
        }
        finally
        {
            _resizeGate.Release();
        }
    }

    private async Task SendStartupCommandsIfNeededAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        if (!IsLive
            || generation != _initializationGeneration
            || SessionClient is null
            || SessionRequest is null
            || StartupCommandDispatcher is null
            || StartupCommandDispatchState is null
            || _inputLeaseId is null)
        {
            return;
        }

        var dispatchState = StartupCommandDispatchState;
        var commands = dispatchState.Commands;
        if (commands.Count == 0)
        {
            return;
        }

        try
        {
            await _physicalInputGate.WaitAsync(cancellationToken);
            try
            {
                if (!IsLive
                    || generation != _initializationGeneration
                    || SessionClient is not { } client
                    || SessionRequest is not { } request
                    || StartupCommandDispatcher is not { } dispatcher
                    || !ReferenceEquals(
                        StartupCommandDispatchState,
                        dispatchState)
                    || _inputLeaseId is not { } leaseId)
                {
                    return;
                }

                var result = await dispatchState.DispatchIfNeededAsync(
                    request.Owner.PanelId,
                    (batchContext, token) => dispatcher.DispatchAsync(
                        client,
                        request.SessionId,
                        leaseId,
                        commands,
                        batchContext,
                        token),
                    cancellationToken);
                if (result is not null)
                {
                    // Observational only: the runtime-owned state published the authoritative
                    // typed outcome directly to its VM before returning it here.
                    StartupCommandDispatchCompleted?.Invoke(
                        this,
                        new TerminalStartupCommandDispatchEventArgs(
                            dispatchState.Context,
                            result));
                }
            }
            finally
            {
                _physicalInputGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace(
                "terminal.startup-command-dispatch.failed",
                exception);
        }
    }

    private void ApplySnapshot(SessionSnapshot snapshot)
    {
        var descriptor = snapshot.Descriptor;
        switch (descriptor.Lifecycle)
        {
            case SessionLifecycle.Starting:
                SetState(TerminalHostState.Starting, descriptor.StatusDetail);
                break;
            case SessionLifecycle.Active when descriptor.Health == SessionHealth.Healthy:
                SetState(TerminalHostState.Live, descriptor.StatusDetail);
                break;
            case SessionLifecycle.Active:
                SetState(TerminalHostState.Unavailable, descriptor.StatusDetail);
                break;
            case SessionLifecycle.Closing:
                SetState(TerminalHostState.Starting, descriptor.StatusDetail);
                break;
            case SessionLifecycle.Closed:
                SetState(TerminalHostState.Exited, descriptor.StatusDetail);
                break;
            case SessionLifecycle.Failed:
                SetState(TerminalHostState.Error, descriptor.StatusDetail);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(snapshot),
                    descriptor.Lifecycle,
                    "Unknown session lifecycle state.");
        }

        var failureCode = descriptor.Failure?.StableCode;
        if (_lastNotifiedLifecycle != descriptor.Lifecycle
            || _lastNotifiedHealth != descriptor.Health
            || !string.Equals(_lastNotifiedFailureCode, failureCode, StringComparison.Ordinal))
        {
            _lastNotifiedLifecycle = descriptor.Lifecycle;
            _lastNotifiedHealth = descriptor.Health;
            _lastNotifiedFailureCode = failureCode;
            SessionSnapshotChanged?.Invoke(this, new TerminalSessionSnapshotEventArgs(snapshot));
        }
    }

    private void SetState(TerminalHostState state, string? detail = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetState(state, detail));
            return;
        }

        IsLive = state == TerminalHostState.Live;
        InitializationError = state is TerminalHostState.Error or TerminalHostState.Unavailable
            ? detail
            : null;
        ShowFallback = state is TerminalHostState.Error
            or TerminalHostState.Unavailable
            or TerminalHostState.Exited;
        (StatusText, StatusBrush, StatusMessage) = state switch
        {
            TerminalHostState.Waiting => ("Waiting", StartingBrush, string.Empty),
            TerminalHostState.Starting => ("Starting", StartingBrush, detail ?? string.Empty),
            TerminalHostState.Live => ("Live", LiveBrush, string.Empty),
            TerminalHostState.Unavailable =>
                ("Unavailable", UnavailableBrush, detail ?? "Terminal unavailable."),
            TerminalHostState.Exited =>
                ("Exited", ExitedBrush, detail ?? "The terminal session ended."),
            TerminalHostState.Error =>
                ("Error", UnavailableBrush, detail ?? "Unable to start the terminal."),
            TerminalHostState.Stopped => ("Stopped", StartingBrush, string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
    }

    private void DetachRenderer()
    {
        if (_attachmentId is not { } attachmentId
            || _attachedClient is not { } client
            || _attachedClientId is not { } clientId
            || _attachedSessionId is not { } sessionId)
        {
            return;
        }

        _attachmentId = null;
        _inputLeaseId = null;
        _attachedClient = null;
        _attachedClientId = null;
        _attachedSessionId = null;
        var result = client.DetachAsync(
            new DetachSessionRequest(attachmentId, sessionId),
            OperationContext.ForHuman(clientId),
            CancellationToken.None);
        if (!result.IsCompletedSuccessfully)
        {
            _ = ObserveDetachAsync(result);
        }
    }

    private static async Task ObserveDetachAsync(ValueTask<HostResult<Unit>> result)
    {
        try
        {
            _ = await result;
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace(
                "terminal.renderer-detach.failed",
                exception);
        }
    }

    private static async ValueTask DetachStaleAttachmentAsync(
        ISessionHostClient client,
        ClientId clientId,
        SessionId sessionId,
        AttachmentId attachmentId)
    {
        try
        {
            _ = await client.DetachAsync(
                new DetachSessionRequest(attachmentId, sessionId),
                OperationContext.ForHuman(clientId),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace(
                "terminal.stale-renderer-detach.failed",
                exception);
        }
    }

    private void OnSurfaceViewportChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        // Unconditionally, even with no attachment yet: a change dropped here
        // is a change nothing else will report again. The resize itself already
        // declines when there is nothing attached to resize.
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private async void OnResizeTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _resizeTimer.Stop();
        await ResizeTerminalAsync();
    }

    private void OnSurfaceGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        _focusRequested = true;
        if (_surface.IsInputReady)
        {
            _ = FocusTerminalAsync();
        }
    }

    private void OnSurfaceLostFocus(object? sender, FocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        _focusRequested = false;
        _ = BlurTerminalAsync();
    }

    private void OnSurfaceFocusRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RequestInputFocus();
    }

    private void RestoreRequestedFocus()
    {
        if (!_focusRequested || !_surface.IsInputReady)
        {
            return;
        }

        RequestInputFocus();
    }

    private ViewportDescriptor CurrentViewport() => _surface.CurrentViewport(
        TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);

    private OperationContext NewContext() => OperationContext.ForHuman(
        ClientId ?? throw new InvalidOperationException("The desktop client identity is unavailable."));

    private ISessionHostClient RequireClient() => SessionClient
        ?? throw new InvalidOperationException("The session-host client is unavailable.");

    private EnsureTerminalSessionRequest RequireSessionRequest() => SessionRequest
        ?? throw new InvalidOperationException("The terminal session request is unavailable.");

    private InputLeaseId RequireInputLease() => _inputLeaseId
        ?? throw new InvalidOperationException("The terminal input lease is unavailable.");

    private static T RequireSuccess<T>(HostResult<T> result) => result switch
    {
        HostResult<T>.Success success => success.Value,
        HostResult<T>.Failure failure => throw new InvalidOperationException(
            $"{failure.Error.StableCode}: {failure.Error.Message}"),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static T RequirePollingSuccess<T>(HostResult<T> result) => result switch
    {
        HostResult<T>.Success success => success.Value,
        HostResult<T>.Failure failure => throw new HostOperationException(failure.Error),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private sealed class HostOperationException(HostError error)
        : InvalidOperationException($"{error.StableCode}: {error.Message}")
    {
        public HostError Error { get; } = error;

    }

    private static CapabilitySet InteractiveCapabilities() => new(
    [
        SessionCapabilities.AttachInteractive,
        SessionCapabilities.InputLease,
        SessionCapabilities.ManagedRenderer,
        SessionCapabilities.TerminalAgentInputBarrier,
        SessionCapabilities.TerminalFocus,
        SessionCapabilities.TerminalReadScreen,
        SessionCapabilities.TerminalResize,
        SessionCapabilities.TerminalWrite,
        SessionCapabilities.TerminalSendKeys,
        SessionCapabilities.TerminalMouse,
        SessionCapabilities.TerminalScrollback,
        SessionCapabilities.TerminalClearScrollback,
        SessionCapabilities.TerminalFind,
        SessionCapabilities.TerminalSelection,
        SessionCapabilities.TerminalPaste,
    ]);

    private enum TerminalHostState
    {
        Waiting,
        Starting,
        Live,
        Unavailable,
        Exited,
        Error,
        Stopped,
    }

    private sealed record HumanPtyInputAuthority(
        ISessionHostClient Client,
        SessionId SessionId,
        InputLeaseId LeaseId,
        OperationContext Context);

    private sealed record PendingTerminalResize(
        long Generation,
        ISessionHostClient Client,
        SessionId SessionId,
        AttachmentId AttachmentId,
        ViewportDescriptor Viewport,
        OperationContext Context,
        CancellationToken CancellationToken);
}
