using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private const string BrowserInteractionOutcomeUnknownStableCode =
        "browser_interaction_outcome_unknown";

    public async ValueTask<HostResult<AgentBrowserActionResult>>
        RunAgentBrowserActionAsync(
            AgentAuthorizationId authorizationId,
            AgentBrowserAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentBrowserActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentBrowserActionResult>(
                "The governed browser-agent execution bridge is not composed.",
                revision: 0);
        }

        AgentBrowserDispatch? dispatch = null;
        AgentActionPermit? permit = null;
        HostResult<AgentBrowserActionResult>? preDispatchFailure = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentBrowserActionResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var sessionId = GetBrowserRequestSessionId(action.Request);
            var exactContextResult = ResolveExactAgentContext(
                action.Proposal.Target);
            if (exactContextResult
                is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentBrowserActionResult>.Fail(
                    contextFailure.Error,
                    contextFailure.CurrentRevision);
            }

            var exactContext =
                ((HostResult<AgentContextSnapshot>.Success)exactContextResult).Value;
            revision = exactContext.Revision;
            var exactSessionPanel = exactContext.Panels
                .SingleOrDefault(panel => panel.SessionId == sessionId);
            if (exactSessionPanel?.SessionRevision
                is not long expectedSessionRevision)
            {
                return InvalidAgentBrowserAction(
                    "The exact browser context has no matching live session revision.",
                    revision);
            }

            if (!TryGetSession(sessionId, out var session))
            {
                return NotFound<AgentBrowserActionResult>("session", revision);
            }

            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentBrowserActionComposer.BindForExecution(
                    action,
                    exactContext);
                dispatch = CaptureAgentBrowserDispatch(
                    action.Request,
                    session,
                    revision,
                    expectedSessionRevision);
            }
            catch (AgentBrowserDispatchException exception)
            {
                return HostResult<AgentBrowserActionResult>.Fail(
                    exception.Error,
                    revision);
            }
            catch (ArgumentException)
            {
                return InvalidAgentBrowserAction(
                    "The prepared action no longer matches the exact live browser target.",
                    revision);
            }
            catch (InvalidOperationException)
            {
                return InvalidAgentBrowserAction(
                    "The prepared action no longer matches its typed browser request.",
                    revision);
            }

            var permitResult = await _agentAuthorizationConsumer
                .ConsumeAsync(
                    authorizationId,
                    binding,
                    cancellationToken)
                .ConfigureAwait(false);
            if (permitResult is AgentPermitResult.Denied denied)
            {
                return MapBrowserAuthorizationFailure(
                    denied.Error,
                    revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            if (dispatch.AttachmentCancellation.IsCancellationRequested)
            {
                preDispatchFailure = BrowserAttachmentRevoked(revision);
            }
            else if (dispatch.AttachmentClientId
                != permit.Authorization.ApprovingClientId)
            {
                preDispatchFailure = InvalidAgentBrowserAction(
                    "The interactive browser attachment is not owned by the approving client.",
                    revision);
            }
            else if (!dispatch.Session.CanExecuteAgentBrowserAction(
                         dispatch.AttachmentId,
                         permit.Authorization.ApprovingClientId,
                         dispatch.ExpectedSessionRevision,
                         dispatch.AttachmentCancellation))
            {
                preDispatchFailure = BrowserAttachmentRevoked(revision);
            }
            else if (AgentBrowserDomainPolicy.Evaluate(
                         dispatch.Request,
                         dispatch.Browser.State,
                         permit.Authorization.Source) is
            { IsAllowed: false, Error: { } policyError })
            {
                preDispatchFailure =
                    HostResult<AgentBrowserActionResult>.Fail(
                        policyError,
                        revision);
            }

            if (preDispatchFailure is null
                && RequiresOneActionInputLease(action.Request))
            {
                var leaseResult = dispatch.Session
                    .AcquireOneActionAgentLease(permit.Authorization);
                if (leaseResult
                    is HostResult<OneActionAgentLease>.Failure leaseFailure)
                {
                    preDispatchFailure =
                        HostResult<AgentBrowserActionResult>.Fail(
                            leaseFailure.Error,
                            leaseFailure.CurrentRevision);
                }
                else
                {
                    var leaseSuccess =
                        (HostResult<OneActionAgentLease>.Success)leaseResult;
                    var lease = leaseSuccess.Value;
                    dispatch = dispatch with
                    {
                        InputCancellation = lease.CancellationToken,
                        OneActionLeaseId = lease.Id,
                        ExpectedSessionRevision =
                            leaseSuccess.ResultingRevision,
                    };
                }
            }
        }
        catch (AgentBrowserDispatchException exception) when (permit is null)
        {
            return HostResult<AgentBrowserActionResult>.Fail(
                exception.Error,
                revision);
        }
        catch (AgentBrowserDispatchException exception)
        {
            preDispatchFailure = HostResult<AgentBrowserActionResult>.Fail(
                exception.Error,
                revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentBrowserActionResult>(revision);
        }
        catch (OperationCanceledException)
        {
            preDispatchFailure =
                Cancelled<AgentBrowserActionResult>(revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentBrowserActionResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            preDispatchFailure =
                Cancelled<AgentBrowserActionResult>(revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentBrowserActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The browser authorization broker is unavailable.",
                    retryable: true),
                revision);
        }
        catch (Exception)
        {
            preDispatchFailure = HostResult<AgentBrowserActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The governed browser action could not be prepared.",
                    retryable: true),
                revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (preDispatchFailure is not null)
        {
            return await CompleteAgentBrowserPreDispatchFailureAsync(
                    dispatch!,
                    permit!,
                    preDispatchFailure,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await DispatchAndCompleteAgentBrowserActionAsync(
                dispatch!,
                permit!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static AgentBrowserDispatch CaptureAgentBrowserDispatch(
        AgentBrowserRequest request,
        HostedSession session,
        long revision,
        long expectedSessionRevision)
    {
        var snapshot = session.Snapshot().Descriptor;
        if (snapshot.Lifecycle != SessionLifecycle.Active)
        {
            throw BrowserDispatchFailure(
                HostErrorCode.SessionClosed,
                "The exact browser session is no longer active.");
        }

        if (session.Engine is not IBrowserPanelSession browser
            || session.Engine.Kind != PanelKind.Browser)
        {
            throw BrowserDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The exact session is not a browser.");
        }

        var requiredCapability = RequiredBrowserCapability(request);
        if (!browser.Capabilities.Contains(requiredCapability))
        {
            throw BrowserDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The browser no longer supports the governed operation.");
        }

        if (RequiresBrowserOriginGuard(request)
            && !browser.Capabilities.Contains(
                SessionCapabilities.BrowserOriginGuard))
        {
            throw BrowserDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The browser cannot contain governed navigation redirects.");
        }

        if (RequiresOneActionInputLease(request)
            && !browser.Capabilities.Contains(
                SessionCapabilities.BrowserAgentInputBarrier))
        {
            throw BrowserDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The browser cannot fence agent input from physical human input.");
        }

        if (!session.TryCaptureAgentBrowserAttachmentAuthority(
                expectedSessionRevision,
                out var attachmentId,
                out var attachmentClientId,
                out var attachmentCancellation))
        {
            throw BrowserDispatchFailure(
                HostErrorCode.LeaseDenied,
                "The exact interactive browser attachment is unavailable or changed.");
        }

        return new AgentBrowserDispatch(
            request,
            session,
            browser,
            session.CaptureRuntimeAuthority(),
            attachmentCancellation,
            attachmentId,
            attachmentClientId,
            expectedSessionRevision,
            revision,
            CancellationToken.None,
            OneActionLeaseId: null);
    }

    private async ValueTask<HostResult<AgentBrowserActionResult>>
        CompleteAgentBrowserPreDispatchFailureAsync(
            AgentBrowserDispatch dispatch,
            AgentActionPermit permit,
            HostResult<AgentBrowserActionResult> failure,
            CancellationToken callerCancellation)
    {
        try
        {
            var hostFailure =
                (HostResult<AgentBrowserActionResult>.Failure)failure;
            var cancelled = permit.CancellationToken.IsCancellationRequested
                || dispatch.RuntimeCancellation.IsCancellationRequested
                || dispatch.AttachmentCancellation.IsCancellationRequested
                || dispatch.InputCancellation.IsCancellationRequested
                || hostFailure.Error.Code == HostErrorCode.Cancelled;
            var stableCode = cancelled
                ? BrowserCancellationCode(
                    dispatch,
                    permit,
                    callerCancellation)
                : hostFailure.Error.StableCode;
            var completion = Completion(
                permit,
                cancelled
                    ? AgentActionOutcome.Cancelled
                    : AgentActionOutcome.Failed,
                stableCode);
            var normalizedFailure = NormalizeAgentBrowserCancellationResult(
                failure,
                completion,
                dispatch.Revision);
            return await CompleteConsumedAgentActionAsync(
                    permit,
                    completion,
                    normalizedFailure,
                    dispatch.Revision)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseOneActionLease(dispatch, permit);
        }
    }

    private async ValueTask<HostResult<AgentBrowserActionResult>>
        DispatchAndCompleteAgentBrowserActionAsync(
            AgentBrowserDispatch dispatch,
            AgentActionPermit permit,
            CancellationToken callerCancellation)
    {
        using var executionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                permit.CancellationToken,
                dispatch.RuntimeCancellation,
                dispatch.AttachmentCancellation,
                dispatch.InputCancellation);
        HostResult<AgentBrowserActionResult> result;
        if (executionCancellation.IsCancellationRequested
            && dispatch.Request is AgentBrowserRequest.Wait wait)
        {
            result = await WaitForBrowserAsync(
                    dispatch,
                    wait.Value,
                    executionCancellation.Token)
                .ConfigureAwait(false);
        }
        else if (executionCancellation.IsCancellationRequested)
        {
            result = Cancelled<AgentBrowserActionResult>(dispatch.Revision);
        }
        else if (!dispatch.Session.CanExecuteAgentBrowserAction(
                     dispatch.AttachmentId,
                     permit.Authorization.ApprovingClientId,
                     dispatch.ExpectedSessionRevision,
                     dispatch.AttachmentCancellation))
        {
            result = BrowserAttachmentRevoked(dispatch.Revision);
        }
        else if (dispatch.OneActionLeaseId is { } leaseId
            && !dispatch.Session.HoldsLease(
                leaseId,
                permit.Authorization.Agent.Id))
        {
            result = Cancelled<AgentBrowserActionResult>(dispatch.Revision);
        }
        else
        {
            var policyDecision = AgentBrowserDomainPolicy.Evaluate(
                dispatch.Request,
                dispatch.Browser.State,
                permit.Authorization.Source);
            if (policyDecision.Error is { } policyError)
            {
                result = HostResult<AgentBrowserActionResult>.Fail(
                    policyError,
                    dispatch.Revision);
            }
            else
            {
                result = await DispatchAgentBrowserActionAsync(
                        dispatch,
                        policyDecision,
                        executionCancellation.Token)
                    .ConfigureAwait(false);
            }
        }

        var completion = CreateAgentBrowserCompletion(
            result,
            dispatch,
            permit,
            callerCancellation);
        var normalizedResult = NormalizeAgentBrowserCancellationResult(
            result,
            completion,
            dispatch.Revision);
        try
        {
            return await CompleteConsumedAgentActionAsync(
                    permit,
                    completion,
                    normalizedResult,
                    dispatch.Revision)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseOneActionLease(dispatch, permit);
        }
    }

    private async ValueTask<HostResult<AgentBrowserActionResult>>
        DispatchAgentBrowserActionAsync(
            AgentBrowserDispatch dispatch,
            AgentBrowserDomainPolicyDecision policyDecision,
            CancellationToken cancellationToken)
    {
        try
        {
            if (dispatch.Request is AgentBrowserRequest.Snapshot snapshot)
            {
                var document = BrowserDocumentBinding.FromState(
                    dispatch.Browser.State);
                var snapshotResult = await dispatch.Browser
                    .CaptureSnapshotAsync(
                        document,
                        cancellationToken,
                        snapshot.Query ?? BrowserSnapshotQuery.Lean)
                    .ConfigureAwait(false);
                return snapshotResult.IsSuccess
                    ? HostResult<AgentBrowserActionResult>.Succeed(
                        new AgentBrowserActionResult.Snapshot(
                            snapshotResult.Value!),
                        dispatch.Session.Snapshot().Descriptor.Revision)
                    : MapBrowserOperationFailure(
                        snapshotResult.Error!,
                        dispatch.Revision);
            }

            if (dispatch.Request is AgentBrowserRequest.Wait wait)
            {
                return await WaitForBrowserAsync(
                        dispatch,
                        wait.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (dispatch.Request is AgentBrowserRequest.Click click)
            {
                var startBinding = RequiredStartBinding(policyDecision);
                var sourceDocument = new BrowserDocumentBinding(
                    startBinding.Address,
                    startBinding.DocumentRevision);
                var reference = new BrowserElementReference(
                    click.Value.Reference,
                    sourceDocument);
                var clickResult = await dispatch.Browser
                    .ClickWithinOriginAsync(
                        reference,
                        RequiredAllowedOrigin(policyDecision),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!clickResult.IsSuccess)
                {
                    return MapBrowserOperationFailure(
                        clickResult.Error!,
                        dispatch.Revision);
                }

                if (clickResult.Value!.SourceDocument != sourceDocument)
                {
                    return HostResult<AgentBrowserActionResult>.Fail(
                        new HostError(
                            HostErrorCode.EngineFailed,
                            BrowserInteractionOutcomeUnknownStableCode,
                            "The browser click receipt did not match its source document."),
                        dispatch.Revision);
                }

                await ReconcileBrowserMutationAsync(dispatch)
                    .ConfigureAwait(false);
                return HostResult<AgentBrowserActionResult>.Succeed(
                    new AgentBrowserActionResult.Completed(),
                    dispatch.Session.Snapshot().Descriptor.Revision);
            }

            if (dispatch.Request is AgentBrowserRequest.Fill fill)
            {
                var startBinding = RequiredStartBinding(policyDecision);
                var sourceDocument = new BrowserDocumentBinding(
                    startBinding.Address,
                    startBinding.DocumentRevision);
                var reference = new BrowserElementReference(
                    fill.Value.Reference,
                    sourceDocument);
                var fillResult = await dispatch.Browser
                    .FillWithinOriginAsync(
                        reference,
                        fill.Value.Text,
                        RequiredAllowedOrigin(policyDecision),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!fillResult.IsSuccess)
                {
                    return MapBrowserOperationFailure(
                        fillResult.Error!,
                        dispatch.Revision);
                }

                if (fillResult.Value!.SourceDocument != sourceDocument)
                {
                    return HostResult<AgentBrowserActionResult>.Fail(
                        new HostError(
                            HostErrorCode.EngineFailed,
                            BrowserInteractionOutcomeUnknownStableCode,
                            "The browser fill receipt did not match its source document."),
                        dispatch.Revision);
                }

                await ReconcileBrowserMutationAsync(dispatch)
                    .ConfigureAwait(false);
                return HostResult<AgentBrowserActionResult>.Succeed(
                    new AgentBrowserActionResult.Completed(),
                    dispatch.Session.Snapshot().Descriptor.Revision);
            }

            if (dispatch.Request is AgentBrowserRequest.Check check)
            {
                var startBinding = RequiredStartBinding(policyDecision);
                var sourceDocument = new BrowserDocumentBinding(
                    startBinding.Address,
                    startBinding.DocumentRevision);
                var reference = new BrowserElementReference(
                    check.Value.Reference,
                    sourceDocument);
                var checkResult = await dispatch.Browser
                    .CheckWithinOriginAsync(
                        reference,
                        RequiredAllowedOrigin(policyDecision),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!checkResult.IsSuccess)
                {
                    return MapBrowserOperationFailure(
                        checkResult.Error!,
                        dispatch.Revision);
                }

                if (checkResult.Value!.SourceDocument != sourceDocument)
                {
                    return HostResult<AgentBrowserActionResult>.Fail(
                        new HostError(
                            HostErrorCode.EngineFailed,
                            BrowserInteractionOutcomeUnknownStableCode,
                            "The browser check receipt did not match its source document."),
                        dispatch.Revision);
                }

                await ReconcileBrowserMutationAsync(dispatch)
                    .ConfigureAwait(false);
                return HostResult<AgentBrowserActionResult>.Succeed(
                    new AgentBrowserActionResult.Completed(),
                    dispatch.Session.Snapshot().Descriptor.Revision);
            }

            if (dispatch.Request is AgentBrowserRequest.Mouse mouse)
            {
                return await DispatchBrowserAutomationAsync(
                        dispatch,
                        mouse.Value.Binding,
                        token => dispatch.Browser.DispatchMouseWithinOriginAsync(
                            mouse.Value,
                            RequiredAllowedOrigin(policyDecision),
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (dispatch.Request is AgentBrowserRequest.Key key)
            {
                return await DispatchBrowserAutomationAsync(
                        dispatch,
                        key.Value.Binding,
                        token => dispatch.Browser.DispatchKeyWithinOriginAsync(
                            key.Value,
                            RequiredAllowedOrigin(policyDecision),
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (dispatch.Request is AgentBrowserRequest.Scroll scroll)
            {
                return await DispatchBrowserAutomationAsync(
                        dispatch,
                        scroll.Value.Binding,
                        token => dispatch.Browser.ScrollWithinOriginAsync(
                            scroll.Value,
                            RequiredAllowedOrigin(policyDecision),
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (dispatch.Request is AgentBrowserRequest.Evaluate evaluate)
            {
                var evaluation = await dispatch.Browser.EvaluateWithinOriginAsync(
                        evaluate.Value,
                        RequiredAllowedOrigin(policyDecision),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!evaluation.IsSuccess)
                {
                    return MapBrowserOperationFailure(
                        evaluation.Error!,
                        dispatch.Revision);
                }

                if (evaluation.Value!.SourceBinding != evaluate.Value.Binding)
                {
                    return UnknownBrowserInteractionOutcome(
                        "The browser evaluation receipt did not match its source binding.",
                        dispatch.Revision);
                }

                await ReconcileBrowserMutationAsync(dispatch).ConfigureAwait(false);
                return HostResult<AgentBrowserActionResult>.Succeed(
                    new AgentBrowserActionResult.Evaluation(evaluation.Value),
                    dispatch.Session.Snapshot().Descriptor.Revision);
            }

            BrowserResult<BrowserSessionState> browserResult;
            var isMutation = true;
            switch (dispatch.Request)
            {
                case AgentBrowserRequest.ReadState:
                    cancellationToken.ThrowIfCancellationRequested();
                    browserResult =
                        BrowserResult<BrowserSessionState>.Success(
                            dispatch.Browser.State);
                    isMutation = false;
                    break;
                case AgentBrowserRequest.Navigate navigate:
                    browserResult = await dispatch.Browser
                        .NavigateWithinOriginAsync(
                            new BrowserOriginConstrainedNavigationRequest.Navigate(
                                navigate.Value.Address),
                            RequiredAllowedOrigin(policyDecision),
                            RequiredStartBinding(policyDecision),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case AgentBrowserRequest.Back:
                    browserResult = await dispatch.Browser
                        .NavigateWithinOriginAsync(
                            new BrowserOriginConstrainedNavigationRequest.Back(),
                            RequiredAllowedOrigin(policyDecision),
                            RequiredStartBinding(policyDecision),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case AgentBrowserRequest.Forward:
                    browserResult = await dispatch.Browser
                        .NavigateWithinOriginAsync(
                            new BrowserOriginConstrainedNavigationRequest.Forward(),
                            RequiredAllowedOrigin(policyDecision),
                            RequiredStartBinding(policyDecision),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case AgentBrowserRequest.Reload:
                    browserResult = await dispatch.Browser
                        .NavigateWithinOriginAsync(
                            new BrowserOriginConstrainedNavigationRequest.Reload(),
                            RequiredAllowedOrigin(policyDecision),
                            RequiredStartBinding(policyDecision),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case AgentBrowserRequest.Stop:
                    browserResult = await dispatch.Browser
                        .StopAsync(cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    return InvalidAgentBrowserAction(
                        "The browser-agent request kind is unsupported.",
                        dispatch.Revision);
            }

            if (!browserResult.IsSuccess)
            {
                return MapBrowserOperationFailure(
                    browserResult.Error!,
                    dispatch.Revision);
            }

            if (isMutation)
            {
                await ReconcileBrowserMutationAsync(dispatch)
                    .ConfigureAwait(false);
                return HostResult<AgentBrowserActionResult>.Succeed(
                    new AgentBrowserActionResult.Completed(),
                    dispatch.Session.Snapshot().Descriptor.Revision);
            }

            return HostResult<AgentBrowserActionResult>.Succeed(
                new AgentBrowserActionResult.State(browserResult.Value!),
                dispatch.Session.Snapshot().Descriptor.Revision);
        }
        catch (OperationCanceledException)
            when (dispatch.Request is AgentBrowserRequest.Fill
                or AgentBrowserRequest.Check
                or AgentBrowserRequest.Mouse
                or AgentBrowserRequest.Key
                or AgentBrowserRequest.Scroll
                or AgentBrowserRequest.Evaluate)
        {
            return UnknownBrowserInteractionOutcome(
                dispatch.Request switch
                {
                    AgentBrowserRequest.Check =>
                        "The browser check outcome could not be determined.",
                    AgentBrowserRequest.Mouse =>
                        "The browser mouse outcome could not be determined.",
                    AgentBrowserRequest.Key =>
                        "The browser key outcome could not be determined.",
                    AgentBrowserRequest.Scroll =>
                        "The browser scroll outcome could not be determined.",
                    AgentBrowserRequest.Evaluate =>
                        "The browser evaluation outcome could not be determined.",
                    _ => "The browser fill outcome could not be determined.",
                },
                dispatch.Revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentBrowserActionResult>(dispatch.Revision);
        }
        catch (Exception)
            when (dispatch.Request is AgentBrowserRequest.Click
                or AgentBrowserRequest.Fill
                or AgentBrowserRequest.Check
                or AgentBrowserRequest.Mouse
                or AgentBrowserRequest.Key
                or AgentBrowserRequest.Scroll
                or AgentBrowserRequest.Evaluate)
        {
            return UnknownBrowserInteractionOutcome(
                dispatch.Request switch
                {
                    AgentBrowserRequest.Fill =>
                        "The browser fill outcome could not be determined.",
                    AgentBrowserRequest.Check =>
                        "The browser check outcome could not be determined.",
                    AgentBrowserRequest.Evaluate =>
                        "The browser evaluation outcome could not be determined.",
                    AgentBrowserRequest.Mouse =>
                        "The browser mouse outcome could not be determined.",
                    AgentBrowserRequest.Key =>
                        "The browser key outcome could not be determined.",
                    AgentBrowserRequest.Scroll =>
                        "The browser scroll outcome could not be determined.",
                    _ =>
                        "The browser click outcome could not be determined.",
                },
                dispatch.Revision);
        }
        catch (Exception)
        {
            return HostResult<AgentBrowserActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The browser engine could not complete the governed action."),
                dispatch.Revision);
        }
    }

    private static async ValueTask<HostResult<AgentBrowserActionResult>>
        DispatchBrowserAutomationAsync(
            AgentBrowserDispatch dispatch,
            BrowserAutomationBinding sourceBinding,
            Func<CancellationToken,
                ValueTask<BrowserResult<BrowserAutomationReceipt>>> operation,
            CancellationToken cancellationToken)
    {
        var result = await operation(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return MapBrowserOperationFailure(result.Error!, dispatch.Revision);
        }

        if (result.Value!.SourceBinding != sourceBinding)
        {
            return UnknownBrowserInteractionOutcome(
                "The browser automation receipt did not match its source binding.",
                dispatch.Revision);
        }

        await ReconcileBrowserMutationAsync(dispatch).ConfigureAwait(false);
        return HostResult<AgentBrowserActionResult>.Succeed(
            new AgentBrowserActionResult.Automation(result.Value),
            dispatch.Session.Snapshot().Descriptor.Revision);
    }

    private static async ValueTask ReconcileBrowserMutationAsync(
        AgentBrowserDispatch dispatch)
    {
        try
        {
            var engineSnapshot = await dispatch.Browser
                .SnapshotAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (!dispatch.Session.ApplyEngineSnapshot(engineSnapshot))
            {
                dispatch.Session.RecordStateChange(
                    "Governed browser action completed.");
            }
        }
        catch (Exception)
        {
            // The browser already reported the action as successful. Preserve
            // that effect and leave later host reconciliation to refresh state.
            dispatch.Session.RecordStateChange(
                "Governed browser action completed; state reconciliation deferred.");
        }
    }

    private AgentActionCompletion CreateAgentBrowserCompletion(
        HostResult<AgentBrowserActionResult> result,
        AgentBrowserDispatch dispatch,
        AgentActionPermit permit,
        CancellationToken callerCancellation)
    {
        var (outcome, stableCode) = result switch
        {
            HostResult<AgentBrowserActionResult>.Success
            {
                Value: AgentBrowserActionResult.Wait
                {
                    Value.Completion: BrowserWaitCompletion.Cancelled,
                },
            } =>
                (
                    AgentActionOutcome.Cancelled,
                    BrowserCancellationCode(
                        dispatch,
                        permit,
                        callerCancellation)),
            HostResult<AgentBrowserActionResult>.Success
            {
                Value: AgentBrowserActionResult.Wait
                {
                    Value.Completion: BrowserWaitCompletion.SessionEnded,
                },
            } =>
                (AgentActionOutcome.Failed, "browser_wait_session_ended"),
            HostResult<AgentBrowserActionResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (
                    AgentActionOutcome.Cancelled,
                    BrowserCancellationCode(
                        dispatch,
                        permit,
                        callerCancellation)),
            HostResult<AgentBrowserActionResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode),
            HostResult<AgentBrowserActionResult>.Success =>
                (
                    AgentActionOutcome.Succeeded,
                    BrowserSuccessCode(dispatch.Request)),
            _ => throw new InvalidOperationException(
                "A browser-agent dispatch returned an unknown result."),
        };
        return Completion(permit, outcome, stableCode);
    }

    private static HostResult<AgentBrowserActionResult>
        NormalizeAgentBrowserCancellationResult(
            HostResult<AgentBrowserActionResult> result,
            AgentActionCompletion completion,
            long revision)
    {
        if (completion.Outcome != AgentActionOutcome.Cancelled)
        {
            return result;
        }

        if (result is HostResult<AgentBrowserActionResult>.Success
            {
                Value: AgentBrowserActionResult.Wait,
            })
        {
            return result;
        }

        return HostResult<AgentBrowserActionResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                completion.StableCode ?? "operation_cancelled",
                "The governed browser action was cancelled."),
            revision);
    }

    private static HostResult<AgentBrowserActionResult>
        MapBrowserOperationFailure(
            BrowserError error,
            long revision)
    {
        var hostCode = error.Code switch
        {
            BrowserErrorCode.UnsupportedCapability =>
                HostErrorCode.CapabilityNotSupported,
            BrowserErrorCode.RendererUnavailable =>
                HostErrorCode.EngineFailed,
            BrowserErrorCode.HistoryUnavailable =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.NavigationInProgress =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.NavigationStateChanged =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.NavigationPolicyDenied =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.NavigationFailed =>
                HostErrorCode.EngineFailed,
            BrowserErrorCode.SnapshotInvalid =>
                HostErrorCode.EngineFailed,
            BrowserErrorCode.ElementReferenceStale =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.ElementNotInteractable =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.ElementNotFillable =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.ElementNotCheckable =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.CheckStateNotApplied =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.ScriptRejected =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.ScriptResultRejected =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.FillValueNotSupported =>
                HostErrorCode.InvalidRequest,
            BrowserErrorCode.InteractionOutcomeUnknown =>
                HostErrorCode.EngineFailed,
            BrowserErrorCode.SessionClosed =>
                HostErrorCode.SessionClosed,
            BrowserErrorCode.Cancelled =>
                HostErrorCode.Cancelled,
            BrowserErrorCode.EngineFailed =>
                HostErrorCode.EngineFailed,
            _ => HostErrorCode.EngineFailed,
        };
        return HostResult<AgentBrowserActionResult>.Fail(
            new HostError(
                hostCode,
                error.StableCode,
                "The browser engine rejected the governed action.",
                error.Code is
                    not BrowserErrorCode.ElementNotInteractable
                    and not BrowserErrorCode.ElementNotFillable
                    and not BrowserErrorCode.ElementNotCheckable
                    and not BrowserErrorCode.FillValueNotSupported
                    and not BrowserErrorCode.InteractionOutcomeUnknown && error.Retryable),
            revision);
    }

    private static HostResult<AgentBrowserActionResult>
        BrowserAttachmentRevoked(long revision) =>
        HostResult<AgentBrowserActionResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                "attachment_revoked",
                "The exact interactive browser attachment was revoked."),
            revision);

    private static string BrowserCancellationCode(
        AgentBrowserDispatch dispatch,
        AgentActionPermit permit,
        CancellationToken callerCancellation)
    {
        if (permit.CancellationToken.IsCancellationRequested)
        {
            return "authority_revoked";
        }

        if (dispatch.RuntimeCancellation.IsCancellationRequested)
        {
            return "session_revoked";
        }

        if (dispatch.AttachmentCancellation.IsCancellationRequested)
        {
            return "attachment_revoked";
        }

        if (dispatch.InputCancellation.IsCancellationRequested)
        {
            return "human_input_preempted";
        }

        return callerCancellation.IsCancellationRequested
            ? "caller_cancelled"
            : "operation_cancelled";
    }

    private static string BrowserSuccessCode(AgentBrowserRequest request) =>
        request switch
        {
            AgentBrowserRequest.ReadState => "state_read",
            AgentBrowserRequest.Snapshot => "snapshot_captured",
            AgentBrowserRequest.Wait => "wait_completed",
            AgentBrowserRequest.Click => "click_completed",
            AgentBrowserRequest.Fill => "fill_completed",
            AgentBrowserRequest.Check => "check_completed",
            AgentBrowserRequest.Mouse => "mouse_completed",
            AgentBrowserRequest.Key => "key_completed",
            AgentBrowserRequest.Scroll => "scroll_completed",
            AgentBrowserRequest.Evaluate => "evaluate_completed",
            AgentBrowserRequest.Navigate => "navigate_completed",
            AgentBrowserRequest.Back => "back_completed",
            AgentBrowserRequest.Forward => "forward_completed",
            AgentBrowserRequest.Reload => "reload_completed",
            AgentBrowserRequest.Stop => "stopped",
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.GetType(),
                "The browser-agent request kind is unsupported."),
        };

    private static bool RequiresBrowserOriginGuard(
        AgentBrowserRequest request) =>
        request is AgentBrowserRequest.Navigate
            or AgentBrowserRequest.Click
            or AgentBrowserRequest.Fill
            or AgentBrowserRequest.Check
            or AgentBrowserRequest.Mouse
            or AgentBrowserRequest.Key
            or AgentBrowserRequest.Scroll
            or AgentBrowserRequest.Evaluate
            or AgentBrowserRequest.Back
            or AgentBrowserRequest.Forward
            or AgentBrowserRequest.Reload;

    private static bool RequiresOneActionInputLease(
        AgentBrowserRequest request) =>
        request is not AgentBrowserRequest.ReadState
            and not AgentBrowserRequest.Snapshot
            and not AgentBrowserRequest.Wait;

    private static void ReleaseOneActionLease(
        AgentBrowserDispatch dispatch,
        AgentActionPermit permit)
    {
        if (dispatch.OneActionLeaseId is { } leaseId)
        {
            dispatch.Session.ReleaseOneActionAgentLease(
                leaseId,
                permit.Authorization.Agent.Id);
        }
    }

    private static BrowserNavigationOrigin RequiredAllowedOrigin(
        AgentBrowserDomainPolicyDecision policyDecision) =>
        policyDecision.AllowedOrigin
        ?? throw BrowserDispatchFailure(
            HostErrorCode.InvalidRequest,
            "The governed browser navigation has no approved origin.");

    private static BrowserNavigationStartBinding RequiredStartBinding(
        AgentBrowserDomainPolicyDecision policyDecision) =>
        policyDecision.StartBinding
        ?? throw BrowserDispatchFailure(
            HostErrorCode.InvalidRequest,
            "The governed browser navigation has no starting-document binding.");

    private static string RequiredBrowserCapability(
        AgentBrowserRequest request) =>
        request switch
        {
            AgentBrowserRequest.ReadState =>
                SessionCapabilities.BrowserReadState,
            AgentBrowserRequest.Snapshot =>
                SessionCapabilities.BrowserSnapshot,
            AgentBrowserRequest.Wait =>
                SessionCapabilities.BrowserWait,
            AgentBrowserRequest.Click =>
                SessionCapabilities.BrowserClick,
            AgentBrowserRequest.Fill =>
                SessionCapabilities.BrowserFill,
            AgentBrowserRequest.Check =>
                SessionCapabilities.BrowserCheck,
            AgentBrowserRequest.Mouse =>
                SessionCapabilities.BrowserMouse,
            AgentBrowserRequest.Key =>
                SessionCapabilities.BrowserKey,
            AgentBrowserRequest.Scroll =>
                SessionCapabilities.BrowserScroll,
            AgentBrowserRequest.Evaluate =>
                SessionCapabilities.BrowserEvaluate,
            AgentBrowserRequest.Navigate =>
                SessionCapabilities.BrowserNavigate,
            AgentBrowserRequest.Back =>
                SessionCapabilities.BrowserBack,
            AgentBrowserRequest.Forward =>
                SessionCapabilities.BrowserForward,
            AgentBrowserRequest.Reload =>
                SessionCapabilities.BrowserReload,
            AgentBrowserRequest.Stop =>
                SessionCapabilities.BrowserStop,
            _ => throw BrowserDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The browser-agent request kind is unsupported."),
        };

    private static SessionId GetBrowserRequestSessionId(
        AgentBrowserRequest request) =>
        request switch
        {
            AgentBrowserRequest.ReadState read => read.SessionId,
            AgentBrowserRequest.Snapshot snapshot => snapshot.SessionId,
            AgentBrowserRequest.Wait wait => wait.Value.SessionId,
            AgentBrowserRequest.Click click => click.Value.SessionId,
            AgentBrowserRequest.Fill fill => fill.Value.SessionId,
            AgentBrowserRequest.Check check => check.Value.SessionId,
            AgentBrowserRequest.Mouse mouse => mouse.Value.SessionId,
            AgentBrowserRequest.Key key => key.Value.SessionId,
            AgentBrowserRequest.Scroll scroll => scroll.Value.SessionId,
            AgentBrowserRequest.Evaluate evaluate => evaluate.Value.SessionId,
            AgentBrowserRequest.Navigate navigate => navigate.Value.SessionId,
            AgentBrowserRequest.Back back => back.SessionId,
            AgentBrowserRequest.Forward forward => forward.SessionId,
            AgentBrowserRequest.Reload reload => reload.SessionId,
            AgentBrowserRequest.Stop stop => stop.SessionId,
            _ => throw BrowserDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The browser-agent request kind is unsupported."),
        };

    private static HostResult<AgentBrowserActionResult>
        InvalidAgentBrowserAction(
            string message,
            long revision) =>
        HostResult<AgentBrowserActionResult>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            revision);

    private static HostResult<AgentBrowserActionResult>
        UnknownBrowserInteractionOutcome(
            string message,
            long revision) =>
        HostResult<AgentBrowserActionResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                BrowserInteractionOutcomeUnknownStableCode,
                message,
                Retryable: false),
            revision);

    private static HostResult<AgentBrowserActionResult>
        MapBrowserAuthorizationFailure(
            AgentAuthorizationError error,
            long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                HostError.Create(
                    HostErrorCode.DeadlineExceeded,
                    "The one-action browser authorization has expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                HostError.Create(
                    HostErrorCode.Cancelled,
                    "The browser-agent action was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The browser-agent audit trail is unavailable.",
                    retryable: true),
            _ => HostError.Create(
                HostErrorCode.InvalidRequest,
                "The exact one-action browser authorization was rejected."),
        };
        return HostResult<AgentBrowserActionResult>.Fail(
            hostError,
            revision);
    }

    private static AgentBrowserDispatchException BrowserDispatchFailure(
        HostErrorCode code,
        string message) =>
        new(HostError.Create(code, message));

    private sealed record AgentBrowserDispatch(
        AgentBrowserRequest Request,
        HostedSession Session,
        IBrowserPanelSession Browser,
        CancellationToken RuntimeCancellation,
        CancellationToken AttachmentCancellation,
        AttachmentId AttachmentId,
        ClientId AttachmentClientId,
        long ExpectedSessionRevision,
        long Revision,
        CancellationToken InputCancellation,
        InputLeaseId? OneActionLeaseId);

    private sealed class AgentBrowserDispatchException(HostError error)
        : Exception(error.Message)
    {
        public HostError Error { get; } = error;

    }
}
