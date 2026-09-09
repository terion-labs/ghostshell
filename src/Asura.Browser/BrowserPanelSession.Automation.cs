using Asura.Application;

namespace Asura.Browser;

public sealed partial class BrowserPanelSession
{
    public async ValueTask<BrowserResult<BrowserAutomationReceipt>>
        DispatchMouseWithinOriginAsync(
            BrowserMouseRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserMouse))
        {
            return UnsupportedAutomation<BrowserAutomationReceipt>(
                SessionCapabilities.BrowserMouse);
        }

        var result = await ExecuteGovernedAutomationAsync(
                request.Binding,
                allowedOrigin,
                (renderer, binding, token) =>
                    renderer.DispatchMouseWithinOriginAsync(
                        new BrowserMouseRequest(
                            Id, binding, request.Action, request.XCss, request.YCss,
                            request.Button, request.Buttons, request.Modifiers,
                            request.ClickCount, request.DeltaX, request.DeltaY),
                        allowedOrigin,
                        token),
                cancellationToken)
            .ConfigureAwait(false);
        return ToReceipt(request.Binding, result);
    }

    public async ValueTask<BrowserResult<BrowserAutomationReceipt>>
        DispatchKeyWithinOriginAsync(
            BrowserKeyRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserKey))
        {
            return UnsupportedAutomation<BrowserAutomationReceipt>(
                SessionCapabilities.BrowserKey);
        }

        var result = await ExecuteGovernedAutomationAsync(
                request.Binding,
                allowedOrigin,
                (renderer, binding, token) =>
                    renderer.DispatchKeyWithinOriginAsync(
                        new BrowserKeyRequest(
                            Id, binding, request.Action, request.Key,
                            request.Modifiers),
                        allowedOrigin,
                        token),
                cancellationToken)
            .ConfigureAwait(false);
        return ToReceipt(request.Binding, result);
    }

    public async ValueTask<BrowserResult<BrowserAutomationReceipt>>
        ScrollWithinOriginAsync(
            BrowserScrollRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserScroll))
        {
            return UnsupportedAutomation<BrowserAutomationReceipt>(
                SessionCapabilities.BrowserScroll);
        }

        var result = await ExecuteGovernedAutomationAsync(
                request.Binding,
                allowedOrigin,
                (renderer, binding, token) =>
                    renderer.ScrollWithinOriginAsync(
                        new BrowserScrollRequest(
                            Id, binding, request.OriginXCss, request.OriginYCss,
                            request.DeltaX, request.DeltaY, request.Modifiers),
                        allowedOrigin,
                        token),
                cancellationToken)
            .ConfigureAwait(false);
        return ToReceipt(request.Binding, result);
    }

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
            return UnsupportedAutomation<BrowserEvaluationResult>(
                SessionCapabilities.BrowserEvaluate);
        }

        var result = await ExecuteGovernedAutomationAsync(
                request.Binding,
                allowedOrigin,
                async (renderer, binding, token) =>
                {
                    var rendererResult = await renderer.EvaluateWithinOriginAsync(
                            new BrowserEvaluateRequest(
                                Id, binding, request.Source, request.World,
                                request.AwaitPromise, request.Timeout),
                            allowedOrigin,
                            token)
                        .ConfigureAwait(false);
                    return rendererResult.IsSuccess
                        ? BrowserResult<BrowserAutomationReceipt>.Success(
                            new BrowserAutomationReceipt(
                                rendererResult.Value!.SourceBinding,
                                rendererResult.Value.FreshState))
                        : BrowserResult<BrowserAutomationReceipt>.Failure(
                            rendererResult.Error!);
                },
                cancellationToken,
                evaluation: async (renderer, binding, token) =>
                    await renderer.EvaluateWithinOriginAsync(
                            new BrowserEvaluateRequest(
                                Id, binding, request.Source, request.World,
                                request.AwaitPromise, request.Timeout),
                            allowedOrigin,
                            token)
                        .ConfigureAwait(false))
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return BrowserResult<BrowserEvaluationResult>.Failure(result.Error!);
        }

        try
        {
            return BrowserResult<BrowserEvaluationResult>.Success(
                new BrowserEvaluationResult(
                    request.Binding,
                    result.Value!.FreshState,
                    result.Value.ResultJson ?? "null"));
        }
        catch (ArgumentException)
        {
            return BrowserResult<BrowserEvaluationResult>.Failure(
                BrowserError.Create(
                    BrowserErrorCode.ScriptResultRejected,
                    "The renderer returned an invalid script result."));
        }
    }

    private async ValueTask<BrowserResult<RendererAutomationCompletion>>
        ExecuteGovernedAutomationAsync(
            BrowserAutomationBinding logicalBinding,
            BrowserNavigationOrigin allowedOrigin,
            Func<IBrowserRenderer, BrowserAutomationBinding, CancellationToken,
                ValueTask<BrowserResult<BrowserAutomationReceipt>>> operation,
            CancellationToken cancellationToken,
            Func<IBrowserRenderer, BrowserAutomationBinding, CancellationToken,
                ValueTask<BrowserResult<BrowserEvaluationResult>>>? evaluation = null)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AutomationCancelled();
        }

        using var governedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        BeginGovernedOperation(governedCancellation);
        try
        {
            IBrowserRenderer renderer;
            BrowserAutomationBinding rendererBinding;
            lock (_gate)
            {
                if (_closed || _disposed)
                {
                    return BrowserResult<RendererAutomationCompletion>.Failure(
                        BrowserError.Create(
                            BrowserErrorCode.SessionClosed,
                            "The browser session is closed."));
                }

                if (_renderer is null)
                {
                    return AutomationUnavailable();
                }

                renderer = _renderer;
                var rendererState = renderer.State;
                if (!logicalBinding.Matches(_state)
                    || _rendererStateAtLastProjection != rendererState
                    || rendererState.Address != _state.Address
                    || rendererState.Viewport != _state.Viewport
                    || rendererState.ViewportRevision != _state.ViewportRevision
                    || rendererState.InputEpoch != _state.InputEpoch)
                {
                    return AutomationStateChanged();
                }

                rendererBinding = BrowserAutomationBinding.FromState(rendererState);
            }

            BrowserResult<BrowserAutomationReceipt>? inputResult = null;
            BrowserResult<BrowserEvaluationResult>? evaluationResult = null;
            try
            {
                if (evaluation is null)
                {
                    inputResult = await operation(
                            renderer,
                            rendererBinding,
                            governedCancellation.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    evaluationResult = await evaluation(
                            renderer,
                            rendererBinding,
                            governedCancellation.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return AutomationOutcomeUnknown();
            }

            if (inputResult is { IsSuccess: false })
            {
                return BrowserResult<RendererAutomationCompletion>.Failure(
                    inputResult.Error!);
            }

            if (evaluationResult is { IsSuccess: false })
            {
                return BrowserResult<RendererAutomationCompletion>.Failure(
                    evaluationResult.Error!);
            }

            var returnedBinding = inputResult?.Value!.SourceBinding
                ?? evaluationResult!.Value!.SourceBinding;
            if (returnedBinding != rendererBinding)
            {
                return AutomationOutcomeUnknown();
            }

            ApplyRendererState(renderer, renderer.State);
            lock (_gate)
            {
                if (!ReferenceEquals(_renderer, renderer)
                    || _rendererStateAtLastProjection is null)
                {
                    return AutomationOutcomeUnknown();
                }
            }

            return BrowserResult<RendererAutomationCompletion>.Success(
                new RendererAutomationCompletion(
                    State,
                    evaluationResult?.Value!.Json));
        }
        finally
        {
            await CompleteGovernedOperationAsync().ConfigureAwait(false);
        }
    }

    private static BrowserResult<BrowserAutomationReceipt> ToReceipt(
        BrowserAutomationBinding logicalBinding,
        BrowserResult<RendererAutomationCompletion> result) =>
        result.IsSuccess
            ? BrowserResult<BrowserAutomationReceipt>.Success(
                new BrowserAutomationReceipt(
                    logicalBinding,
                    result.Value!.FreshState))
            : BrowserResult<BrowserAutomationReceipt>.Failure(result.Error!);

    private static BrowserResult<T> UnsupportedAutomation<T>(string capability) =>
        BrowserResult<T>.Failure(
            BrowserError.Create(
                BrowserErrorCode.UnsupportedCapability,
                $"The browser capability '{capability}' is unavailable."));

    private static BrowserResult<RendererAutomationCompletion> AutomationCancelled() =>
        BrowserResult<RendererAutomationCompletion>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser automation was cancelled.",
                retryable: true));

    private static BrowserResult<RendererAutomationCompletion> AutomationUnavailable() =>
        BrowserResult<RendererAutomationCompletion>.Failure(
            BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                "The browser renderer is unavailable.",
                retryable: true));

    private static BrowserResult<RendererAutomationCompletion> AutomationStateChanged() =>
        BrowserResult<RendererAutomationCompletion>.Failure(
            BrowserError.Create(
                BrowserErrorCode.NavigationStateChanged,
                "The browser document, viewport, or input epoch changed.",
                retryable: true));

    private static BrowserResult<RendererAutomationCompletion> AutomationOutcomeUnknown() =>
        BrowserResult<RendererAutomationCompletion>.Failure(
            BrowserError.Create(
                BrowserErrorCode.InteractionOutcomeUnknown,
                "The browser automation outcome is unknown."));

    private sealed record RendererAutomationCompletion(
        BrowserSessionState FreshState,
        string? ResultJson);
}
