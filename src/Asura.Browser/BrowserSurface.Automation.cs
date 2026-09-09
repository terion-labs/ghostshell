using Asura.Application;
using Avalonia;
using Avalonia.Controls;

namespace Asura.Browser;

public sealed partial class BrowserSurface
{
    private static readonly TimeSpan DefaultNativeInputDeadline =
        TimeSpan.FromSeconds(5);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && !_disposed && State is not null)
        {
            PublishLayoutViewport();
        }
    }

    public ValueTask<BrowserResult<BrowserAutomationReceipt>>
        DispatchMouseWithinOriginAsync(
            BrowserMouseRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken) =>
        RunInputAutomationAsync(
            request,
            allowedOrigin,
            SessionCapabilities.BrowserMouse,
            nativeView => nativeView.DispatchMouseAsync(request),
            cancellationToken);

    public ValueTask<BrowserResult<BrowserAutomationReceipt>>
        DispatchKeyWithinOriginAsync(
            BrowserKeyRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken) =>
        RunInputAutomationAsync(
            request,
            allowedOrigin,
            SessionCapabilities.BrowserKey,
            nativeView => nativeView.DispatchKeyAsync(request),
            cancellationToken);

    public ValueTask<BrowserResult<BrowserAutomationReceipt>>
        ScrollWithinOriginAsync(
            BrowserScrollRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken) =>
        RunInputAutomationAsync(
            request,
            allowedOrigin,
            SessionCapabilities.BrowserScroll,
            nativeView => nativeView.DispatchScrollAsync(request),
            cancellationToken);

    public async ValueTask<BrowserResult<BrowserEvaluationResult>>
        EvaluateWithinOriginAsync(
            BrowserEvaluateRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserEvaluate))
        {
            return UnsupportedCapability<BrowserEvaluationResult>(
                SessionCapabilities.BrowserEvaluate);
        }

        var completion = await RunBrowserAutomationAsync(
                request.Binding,
                allowedOrigin,
                request.Timeout,
                advancesInputEpoch: false,
                nativeView => nativeView.EvaluateAsync(request),
                cancellationToken)
            .ConfigureAwait(false);
        if (!completion.IsSuccess)
        {
            return BrowserResult<BrowserEvaluationResult>.Failure(completion.Error!);
        }

        try
        {
            return BrowserResult<BrowserEvaluationResult>.Success(
                new BrowserEvaluationResult(
                    request.Binding,
                    completion.Value!.FreshState,
                    completion.Value.ResultJson ?? "null"));
        }
        catch (Exception exception) when (
            exception is ArgumentException or System.Text.Json.JsonException)
        {
            return BrowserResult<BrowserEvaluationResult>.Failure(
                BrowserError.Create(
                    BrowserErrorCode.ScriptResultRejected,
                    "The evaluated value was not a bounded secret-free JSON value."));
        }
    }

    private async ValueTask<BrowserResult<BrowserAutomationReceipt>>
        RunInputAutomationAsync<TRequest>(
            TRequest request,
            BrowserNavigationOrigin allowedOrigin,
            string capability,
            Func<IEmbeddedBrowserView, Task<NativeBrowserAutomationResult>> dispatch,
            CancellationToken cancellationToken)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(capability))
        {
            return UnsupportedCapability<BrowserAutomationReceipt>(capability);
        }

        var binding = request switch
        {
            BrowserMouseRequest mouse => mouse.Binding,
            BrowserKeyRequest key => key.Binding,
            BrowserScrollRequest scroll => scroll.Binding,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        var completion = await RunBrowserAutomationAsync(
                binding,
                allowedOrigin,
                DefaultNativeInputDeadline,
                advancesInputEpoch: true,
                dispatch,
                cancellationToken)
            .ConfigureAwait(false);
        return completion.IsSuccess
            ? BrowserResult<BrowserAutomationReceipt>.Success(
                new BrowserAutomationReceipt(
                    binding,
                    completion.Value!.FreshState))
            : BrowserResult<BrowserAutomationReceipt>.Failure(completion.Error!);
    }

    private async ValueTask<BrowserResult<NativeAutomationCompletion>>
        RunBrowserAutomationAsync(
            BrowserAutomationBinding binding,
            BrowserNavigationOrigin allowedOrigin,
            TimeSpan deadline,
            bool advancesInputEpoch,
            Func<IEmbeddedBrowserView, Task<NativeBrowserAutomationResult>> dispatch,
            CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return AutomationCancelled();
        }

        if (!_nativeView.SupportsPeerBoundTransport)
        {
            return PeerBoundTransportUnavailable<NativeAutomationCompletion>();
        }

        NativeBrowserViewport nativeViewport;
        try
        {
            nativeViewport = await _nativeView.ReadViewportAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AutomationCancelled();
        }
        catch (Exception)
        {
            return BrowserResult<NativeAutomationCompletion>.Failure(
                BrowserError.Create(
                    BrowserErrorCode.RendererUnavailable,
                    "The browser viewport could not be revalidated before dispatch.",
                    retryable: true));
        }

        try
        {
            Task<BrowserResult<NativeAutomationCompletion>> completion;
            if (_dispatcher.CheckAccess())
            {
                completion = BeginBrowserAutomation(
                    binding,
                    allowedOrigin,
                    nativeViewport,
                    deadline,
                    advancesInputEpoch,
                    dispatch,
                    cancellationToken);
            }
            else
            {
                completion = await _dispatcher.InvokeAsync(
                    () => BeginBrowserAutomation(
                        binding,
                        allowedOrigin,
                        nativeViewport,
                        deadline,
                        advancesInputEpoch,
                        dispatch,
                        cancellationToken));
            }

            return await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // BeginBrowserAutomation never throws cancellation after commit;
            // its pending observer maps that path to outcome-unknown.
            return AutomationCancelled();
        }
        catch (Exception)
        {
            return BrowserResult<NativeAutomationCompletion>.Failure(
                InteractionOutcomeUnknown());
        }
    }

    private Task<BrowserResult<NativeAutomationCompletion>> BeginBrowserAutomation(
        BrowserAutomationBinding binding,
        BrowserNavigationOrigin allowedOrigin,
        NativeBrowserViewport nativeViewport,
        TimeSpan deadline,
        bool advancesInputEpoch,
        Func<IEmbeddedBrowserView, Task<NativeBrowserAutomationResult>> dispatch,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(AutomationCancelled());
        }

        if (_disposed || _interactionRecoveryFailed || HasTimedOutDocumentSnapshot)
        {
            return Task.FromResult(AutomationUnavailable());
        }

        if (State.LoadState != BrowserLoadState.Ready
            || HasPendingElementInteraction
            || _pendingDocumentSnapshot is not null
            || HasGovernedNavigationActivity)
        {
            return Task.FromResult(
                BrowserResult<NativeAutomationCompletion>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationInProgress,
                        "The browser cannot dispatch input while another browser operation is active.",
                        retryable: true)));
        }

        var observedViewport = new BrowserViewportState(
            nativeViewport.WidthCss,
            nativeViewport.HeightCss,
            State.Viewport.DeviceScaleFactor);
        if (observedViewport != State.Viewport)
        {
            PublishViewport(observedViewport);
            return Task.FromResult(AutomationStateChanged());
        }

        if (!binding.Matches(State))
        {
            return Task.FromResult(AutomationStateChanged());
        }

        if (!AllowsGovernedDestination(allowedOrigin, State.Address))
        {
            return Task.FromResult(
                BrowserResult<NativeAutomationCompletion>.Failure(PolicyError()));
        }

        var pending = new PendingBrowserAutomation(
            _nativeView,
            binding,
            allowedOrigin,
            advancesInputEpoch,
            deadline);
        _pendingBrowserAutomation = pending;
        try
        {
            pending.NativeDispatchCommitted = true;
            pending.NativeCompletion = dispatch(_nativeView);
        }
        catch (Exception)
        {
            CompleteAmbiguousBrowserAutomation(pending);
            return pending.Completion.Task;
        }

        pending.CancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var pair = ((BrowserSurface Surface, PendingBrowserAutomation Pending))state!;
                pair.Surface.ScheduleAutomationCancellation(pair.Pending);
            },
            (this, pending));
        _ = ObserveBrowserAutomationDeadlineAsync(pending);
        _ = ObserveNativeBrowserAutomationAsync(pending);
        return pending.Completion.Task;
    }

    private async Task ObserveNativeBrowserAutomationAsync(
        PendingBrowserAutomation pending)
    {
        NativeBrowserAutomationResult result;
        try
        {
            result = await pending.NativeCompletion!.ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = NativeBrowserAutomationResult.OutcomeUnknown();
        }

        // Give CEF's navigation callbacks one dispatcher turn to bind any
        // script/input-triggered top-level navigation before success is final.
        await Task.Yield();
        try
        {
            if (_dispatcher.CheckAccess())
            {
                RecordNativeBrowserAutomation(pending, result);
            }
            else
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    RecordNativeBrowserAutomation(pending, result);
                    return true;
                });
            }
        }
        catch (Exception)
        {
            CompleteAutomationAfterDispatcherFailure(pending);
        }
    }

    private async Task ObserveBrowserAutomationDeadlineAsync(
        PendingBrowserAutomation pending)
    {
        try
        {
            await Task.Delay(pending.Deadline, pending.DeadlineCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ScheduleAutomationCancellation(pending);
    }

    private void ScheduleAutomationCancellation(PendingBrowserAutomation pending)
    {
        _ = CancelAsync();
        return;

        async Task CancelAsync()
        {
            try
            {
                if (_dispatcher.CheckAccess())
                {
                    CompleteAmbiguousBrowserAutomation(pending);
                }
                else
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        CompleteAmbiguousBrowserAutomation(pending);
                        return true;
                    });
                }
            }
            catch (Exception)
            {
                CompleteAutomationAfterDispatcherFailure(pending);
            }
        }
    }

    private void RecordNativeBrowserAutomation(
        PendingBrowserAutomation pending,
        NativeBrowserAutomationResult result)
    {
        if (!ReferenceEquals(_pendingBrowserAutomation, pending)
            || !ReferenceEquals(_nativeView, pending.NativeView))
        {
            return;
        }

        pending.NativeResult = result;
        TryCompleteBrowserAutomation(pending);
    }

    private void TryCompleteBrowserAutomation(PendingBrowserAutomation pending)
    {
        if (!ReferenceEquals(_pendingBrowserAutomation, pending)
            || pending.NativeResult is not { } nativeResult)
        {
            return;
        }

        if (nativeResult.Status == NativeBrowserAutomationStatus.OutcomeUnknown)
        {
            CompleteAmbiguousBrowserAutomation(pending);
            return;
        }

        if (pending.HasObservedNavigationStart && !pending.NavigationTerminal)
        {
            return;
        }

        if (pending.NavigationError is { } navigationError)
        {
            CompleteBrowserAutomationFailure(
                pending,
                navigationError,
                pending.RequiresQuarantine);
            return;
        }

        if (nativeResult.Status == NativeBrowserAutomationStatus.Rejected)
        {
            var error = string.Equals(nativeResult.StableCode, "renderer_unavailable"
, StringComparison.Ordinal) ? BrowserError.Create(
                    BrowserErrorCode.RendererUnavailable,
                    "The native browser renderer is unavailable.",
                    retryable: true)
                : BrowserError.Create(
string.Equals(nativeResult.StableCode, "script_result_not_serializable"
, StringComparison.Ordinal) ? BrowserErrorCode.ScriptResultRejected
                        : BrowserErrorCode.ScriptRejected,
                    "The bounded browser script was rejected.");
            CompleteBrowserAutomation(
                pending,
                BrowserResult<NativeAutomationCompletion>.Failure(error));
            return;
        }

        if (pending.AdvancesInputEpoch)
        {
            if (State.InputEpoch == long.MaxValue)
            {
                CompleteAmbiguousBrowserAutomation(pending);
                return;
            }

            AdvanceInputEpoch();
        }

        CompleteBrowserAutomation(
            pending,
            BrowserResult<NativeAutomationCompletion>.Success(
                new NativeAutomationCompletion(State, nativeResult.ResultJson)));
    }

    private void CompleteAmbiguousBrowserAutomation(
        PendingBrowserAutomation pending)
    {
        if (!ReferenceEquals(_pendingBrowserAutomation, pending))
        {
            pending.Completion.TrySetResult(
                BrowserResult<NativeAutomationCompletion>.Failure(
                    InteractionOutcomeUnknown()));
            return;
        }

        InvalidateElementReferences();
        if (!TryReplaceQuarantinedNativeView())
        {
            _interactionRecoveryFailed = true;
        }

        CompleteBrowserAutomation(
            pending,
            BrowserResult<NativeAutomationCompletion>.Failure(
                InteractionOutcomeUnknown()));
    }

    private void CompleteBrowserAutomationFailure(
        PendingBrowserAutomation pending,
        BrowserError error,
        bool quarantine)
    {
        if (quarantine && !TryReplaceQuarantinedNativeView())
        {
            _interactionRecoveryFailed = true;
        }

        CompleteBrowserAutomation(
            pending,
            BrowserResult<NativeAutomationCompletion>.Failure(error));
    }

    private void CompleteBrowserAutomation(
        PendingBrowserAutomation pending,
        BrowserResult<NativeAutomationCompletion> result)
    {
        if (!ReferenceEquals(_pendingBrowserAutomation, pending))
        {
            return;
        }

        _pendingBrowserAutomation = null;
        pending.CancellationRegistration.Unregister();
        pending.DeadlineCancellation.Cancel();
        pending.DeadlineCancellation.Dispose();
        pending.Completion.TrySetResult(result);
    }

    private void CompleteAutomationAfterDispatcherFailure(
        PendingBrowserAutomation pending)
    {
        _interactionRecoveryFailed = true;
        pending.Completion.TrySetResult(
            BrowserResult<NativeAutomationCompletion>.Failure(
                InteractionOutcomeUnknown()));
    }

    private void PublishLayoutViewport()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Clamp(Bounds.Width, 0, BrowserViewportState.MaximumCssExtent);
        var height = Math.Clamp(Bounds.Height, 0, BrowserViewportState.MaximumCssExtent);
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(scale))
        {
            return;
        }

        PublishViewport(new BrowserViewportState(width, height, scale));
    }

    private void PublishViewport(BrowserViewportState viewport)
    {
        if (State.Viewport == viewport)
        {
            return;
        }

        if (State.ViewportRevision == long.MaxValue)
        {
            _interactionRecoveryFailed = true;
            return;
        }

        Publish(new BrowserSessionState(
            State.Address,
            State.Title,
            State.LoadState,
            State.CanGoBack,
            State.CanGoForward,
            State.DocumentRevision,
            State.Failure,
            viewport,
            State.ViewportRevision + 1,
            State.InputEpoch));
    }

    private void AdvanceInputEpoch()
    {
        if (State.InputEpoch == long.MaxValue)
        {
            throw new InvalidOperationException("The browser input epoch is exhausted.");
        }

        Publish(new BrowserSessionState(
            State.Address,
            State.Title,
            State.LoadState,
            State.CanGoBack,
            State.CanGoForward,
            State.DocumentRevision,
            State.Failure,
            State.Viewport,
            State.ViewportRevision,
            State.InputEpoch + 1));
    }

    private static BrowserResult<NativeAutomationCompletion> AutomationCancelled() =>
        BrowserResult<NativeAutomationCompletion>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser automation was cancelled.",
                retryable: true));

    private static BrowserResult<NativeAutomationCompletion> AutomationUnavailable() =>
        BrowserResult<NativeAutomationCompletion>.Failure(
            BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                "The browser renderer is unavailable.",
                retryable: true));

    private static BrowserResult<NativeAutomationCompletion> AutomationStateChanged() =>
        BrowserResult<NativeAutomationCompletion>.Failure(
            BrowserError.Create(
                BrowserErrorCode.NavigationStateChanged,
                "The browser document, viewport, or input epoch changed before dispatch.",
                retryable: true));

    private sealed record NativeAutomationCompletion(
        BrowserSessionState FreshState,
        string? ResultJson);

    private sealed class PendingBrowserAutomation(
        IEmbeddedBrowserView nativeView,
        BrowserAutomationBinding sourceBinding,
        BrowserNavigationOrigin allowedOrigin,
        bool advancesInputEpoch,
        TimeSpan deadline) :
        PendingElementInteraction(
            nativeView,
            sourceBinding.Document,
            allowedOrigin)
    {
        public BrowserAutomationBinding SourceBinding { get; } = sourceBinding;
        public bool AdvancesInputEpoch { get; } = advancesInputEpoch;
        public TimeSpan Deadline { get; } = deadline;
        public Task<NativeBrowserAutomationResult>? NativeCompletion { get; set; }
        public NativeBrowserAutomationResult? NativeResult { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public TaskCompletionSource<BrowserResult<NativeAutomationCompletion>> Completion
        { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
