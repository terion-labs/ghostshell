using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteBrowserProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        IReadOnlySet<PanelInstanceId> resizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> browserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> fileMetadata,
        CancellationToken cancellationToken)
    {
        if (_agentBrowserHost is null || _browserComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var eligibleBrowsers = context.Panels
            .Where(panel =>
                panel.Kind == PanelKind.Browser
                && browserEligiblePanelIds.Contains(panel.PanelId))
            .ToArray();
        if (eligibleBrowsers.Length == 0)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var exactTarget = context.Target
            is AgentTarget.Panel or AgentTarget.ConnectionSession;
        var parsed = exactTarget
            ? BrowserAgentToolParser.Parse(proposal, eligibleBrowsers.Single())
            : BrowserAgentToolParser.Parse(proposal, eligibleBrowsers);
        if (parsed is BrowserAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var selected = (BrowserAgentIntentResult.Parsed)parsed;
        var panel = context.Panels.SingleOrDefault(
            candidate => candidate.PanelId == selected.PanelId);
        if (panel?.SessionId is not { } sessionId
            || panel.Kind != PanelKind.Browser)
        {
            return CreateRejectedResult(proposal, "target_changed");
        }

        UpdateTargetPresentation(
            context,
            resizeEligiblePanelIds,
            browserEligiblePanelIds,
            fileMetadata);

        AgentBrowserAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            var proposalLifetime = selected.Intent is BrowserAgentIntent.Wait
                ? descriptor.MaximumExecutionLifetime
                    + TimeSpan.FromMinutes(5)
                : ActionLifetime;
            var envelope = new AgentActionEnvelope(
                AgentActionId.New(),
                GetRequiredSession().RunId,
                GetOrCreateAgent(),
                GetPolicyGeneration(),
                now,
                now + proposalLifetime);
            action = _browserComposer.Prepare(
                envelope,
                context,
                CreateBrowserRequest(
                    selected.Intent,
                    sessionId,
                    panel.BrowserMetadata));
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            return CreateRejectedResult(
                proposal,
                "tool_request_rejected",
                panel.PanelId);
        }

        var authorization = await _broker
            .RequestAsync(action.Proposal, cancellationToken)
            .ConfigureAwait(false);
        if (authorization is AgentAuthorizationResult.ApprovalRequired required)
        {
            authorization = await AwaitApprovalAsync(
                    required.Approval,
                    yieldsInput: false,
                    cancellationToken)
                .ConfigureAwait(false);
            descriptor = required.Approval.Tool;
        }

        if (authorization is AgentAuthorizationResult.Denied denied)
        {
            return CreateRejectedResult(
                proposal,
                StableCode(denied.Error.Code),
                panel.PanelId);
        }

        if (authorization
            is not AgentAuthorizationResult.Authorized authorizedResult)
        {
            return CreateRejectedResult(
                proposal,
                "approval_still_required",
                panel.PanelId);
        }

        var actionCancellation = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken,
            selected.PanelId);
        HostResult<AgentBrowserActionResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentBrowserHost
                    .RunAgentBrowserActionAsync(
                        authorizedResult.Authorization.Id,
                        action,
                        actionCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
                when (actionCancellation.Token.IsCancellationRequested)
            {
                hostResult = HostResult<AgentBrowserActionResult>.Fail(
                    new HostError(
                        HostErrorCode.Cancelled,
                        "caller_cancelled",
                        "The browser action was cancelled."),
                    context.Revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
                return CreateRejectedResult(
                    proposal,
                    action.Request is AgentBrowserRequest.Click
                        or AgentBrowserRequest.Fill
                        or AgentBrowserRequest.Check
                        or AgentBrowserRequest.Mouse
                        or AgentBrowserRequest.Key
                        or AgentBrowserRequest.Scroll
                        or AgentBrowserRequest.Evaluate
                        ? BrowserAgentToolResultJson
                            .InteractionOutcomeUnknownStableCode
                        : "browser_host_failed",
                    panel.PanelId);
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation)
                .ConfigureAwait(false);
        }

        hostResult = NormalizeRequestedBrowserActionCancellation(
            hostResult,
            actionCancellation.CancellationRequested
                && !cancellationToken.IsCancellationRequested);
        if (hostResult is HostResult<AgentBrowserActionResult>.Success)
        {
            await RefreshTargetPresentationBestEffortAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return hostResult switch
        {
            HostResult<AgentBrowserActionResult>.Success success =>
                CreateSucceededResult(
                    proposal,
                    success.Value,
                    panel.PanelId),
            HostResult<AgentBrowserActionResult>.Failure failure =>
                CreateFailedResult(
                    proposal,
                    BrowserAgentToolResultJson.ProviderStableCode(
                        failure.Error),
                    BrowserAgentToolResultJson.Failure(
                        failure.Error,
                        panel.PanelId)),
            _ => CreateRejectedResult(
                proposal,
                "browser_action_failed",
                panel.PanelId),
        };
    }

    private static HostResult<AgentBrowserActionResult>
        NormalizeRequestedBrowserActionCancellation(
            HostResult<AgentBrowserActionResult> result,
            bool cancellationRequested)
    {
        if (!cancellationRequested
            || result is not HostResult<AgentBrowserActionResult>.Failure
            {
                Error:
                {
                    Code: HostErrorCode.Cancelled,
                    StableCode: "cancelled" or "operation_cancelled",
                },
            } failure)
        {
            return result;
        }

        return HostResult<AgentBrowserActionResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                "caller_cancelled",
                "The browser action was cancelled."),
            failure.CurrentRevision);
    }

    private static AgentBrowserRequest CreateBrowserRequest(
        BrowserAgentIntent intent,
        SessionId sessionId,
        BrowserSessionMetadata? metadata) =>
        intent switch
        {
            BrowserAgentIntent.ReadState =>
                new AgentBrowserRequest.ReadState(sessionId),
            BrowserAgentIntent.Snapshot snapshot =>
                new AgentBrowserRequest.Snapshot(
                    sessionId,
                    new BrowserSnapshotQuery(
                        snapshot.InteractiveOnly,
                        snapshot.Filter,
                        snapshot.MaximumDepth)),
            BrowserAgentIntent.Wait wait =>
                new AgentBrowserRequest.Wait(
                    new BrowserWaitRequest(
                        sessionId,
                        wait.Condition,
                        wait.Timeout)),
            BrowserAgentIntent.Click click =>
                new AgentBrowserRequest.Click(
                    new BrowserElementClickRequest(
                        sessionId,
                        click.Reference,
                        click.DocumentRevision)),
            BrowserAgentIntent.Fill fill =>
                new AgentBrowserRequest.Fill(
                    new BrowserElementFillRequest(
                        sessionId,
                        fill.Reference,
                        fill.DocumentRevision,
                        fill.Text)),
            BrowserAgentIntent.Check check =>
                new AgentBrowserRequest.Check(
                    new BrowserElementCheckRequest(
                        sessionId,
                        check.Reference,
                        check.DocumentRevision)),
            BrowserAgentIntent.Mouse mouse =>
                new AgentBrowserRequest.Mouse(
                    new BrowserMouseRequest(
                        sessionId,
                        RequireAutomationBinding(
                            metadata,
                            mouse.DocumentRevision,
                            mouse.ViewportRevision,
                            mouse.InputEpoch),
                        mouse.Action,
                        mouse.XCss,
                        mouse.YCss,
                        mouse.Button,
                        mouse.Buttons,
                        mouse.Modifiers,
                        mouse.ClickCount,
                        mouse.DeltaX,
                        mouse.DeltaY)),
            BrowserAgentIntent.Key key =>
                new AgentBrowserRequest.Key(
                    new BrowserKeyRequest(
                        sessionId,
                        RequireAutomationBinding(
                            metadata,
                            key.DocumentRevision,
                            key.ViewportRevision,
                            key.InputEpoch),
                        key.Action,
                        key.KeyValue,
                        key.Modifiers)),
            BrowserAgentIntent.Scroll scroll =>
                new AgentBrowserRequest.Scroll(
                    new BrowserScrollRequest(
                        sessionId,
                        RequireAutomationBinding(
                            metadata,
                            scroll.DocumentRevision,
                            scroll.ViewportRevision,
                            scroll.InputEpoch),
                        scroll.OriginXCss,
                        scroll.OriginYCss,
                        scroll.DeltaX,
                        scroll.DeltaY,
                        scroll.Modifiers)),
            BrowserAgentIntent.Evaluate evaluate =>
                new AgentBrowserRequest.Evaluate(
                    new BrowserEvaluateRequest(
                        sessionId,
                        RequireAutomationBinding(
                            metadata,
                            evaluate.DocumentRevision,
                            evaluate.ViewportRevision,
                            evaluate.InputEpoch),
                        evaluate.Source,
                        evaluate.World,
                        evaluate.AwaitPromise,
                        evaluate.Timeout)),
            BrowserAgentIntent.Navigate navigate =>
                new AgentBrowserRequest.Navigate(
                    new BrowserNavigateRequest(
                        sessionId,
                        navigate.Address)),
            BrowserAgentIntent.Back =>
                new AgentBrowserRequest.Back(sessionId),
            BrowserAgentIntent.Forward =>
                new AgentBrowserRequest.Forward(sessionId),
            BrowserAgentIntent.Reload =>
                new AgentBrowserRequest.Reload(sessionId),
            BrowserAgentIntent.Stop =>
                new AgentBrowserRequest.Stop(sessionId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(intent),
                intent.GetType(),
                "The browser intent is unsupported."),
        };

    private static BrowserAutomationBinding RequireAutomationBinding(
        BrowserSessionMetadata? metadata,
        long documentRevision,
        long viewportRevision,
        long inputEpoch)
    {
        if (metadata is not { Address: { } address }
            || metadata.DocumentRevision != documentRevision
            || metadata.ViewportRevision != viewportRevision
            || metadata.InputEpoch != inputEpoch
            || metadata.Viewport.WidthCss <= 0
            || metadata.Viewport.HeightCss <= 0)
        {
            throw new ArgumentException(
                "The browser document, viewport, or input epoch is stale.",
                nameof(metadata));
        }

        return new BrowserAutomationBinding(
            new BrowserDocumentBinding(address, documentRevision),
            metadata.Viewport,
            viewportRevision,
            inputEpoch);
    }

    private static AgentToolResult CreateSucceededResult(
        AgentToolProposal proposal,
        AgentBrowserActionResult result,
        PanelInstanceId panelId) =>
        new(
            proposal,
            AgentToolResultStatus.Succeeded,
            "tool_succeeded",
            JsonValue(BrowserAgentToolResultJson.Success(result, panelId)));

    private static bool IsBrowserTool(string toolName) =>
        toolName is
            BuiltInAgentTools.BrowserReadState
            or BuiltInAgentTools.BrowserSnapshot
            or BuiltInAgentTools.BrowserWait
            or BuiltInAgentTools.BrowserClick
            or BuiltInAgentTools.BrowserFill
            or BuiltInAgentTools.BrowserCheck
            or BuiltInAgentTools.BrowserMouse
            or BuiltInAgentTools.BrowserKey
            or BuiltInAgentTools.BrowserScroll
            or BuiltInAgentTools.BrowserEvaluate
            or BuiltInAgentTools.BrowserNavigate
            or BuiltInAgentTools.BrowserBack
            or BuiltInAgentTools.BrowserForward
            or BuiltInAgentTools.BrowserReload
            or BuiltInAgentTools.BrowserStop;

    private sealed class BrowserToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context)
        {
            if (runtime._agentBrowserHost is null
                || runtime._browserComposer is null)
            {
                return [];
            }

            if (context.Context.Target is AgentTarget.Workspace)
            {
                return BrowserAgentToolSet.ForWorkspace();
            }

            var eligiblePanels = context.Context.Panels
                .Where(panel =>
                    panel.Kind == PanelKind.Browser
                    && context.BrowserEligiblePanelIds.Contains(panel.PanelId))
                .ToArray();
            if (eligiblePanels.Length == 0)
            {
                return [];
            }

            return context.HasExactTarget
                ? BrowserAgentToolSet.For(eligiblePanels[0])
                : BrowserAgentToolSet.For(eligiblePanels);
        }

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            IsBrowserTool(toolName)
                ? new ResolvedAgentToolContribution(
                    toolName,
                    ExecuteAsync)
                : null;

        private ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken cancellationToken) =>
            runtime.ExecutePanelToolContributionAsync(
                request,
                ExecuteBoundAsync,
                cancellationToken);

        private ValueTask<AgentToolResult> ExecuteBoundAsync(
            AgentToolExecutionRequest request,
            AgentPanelToolContext context,
            CancellationToken cancellationToken) =>
            runtime.ExecuteBrowserProposalAsync(
                request.Proposal,
                request.Descriptor,
                context.Context,
                context.ResizeEligiblePanelIds,
                context.BrowserEligiblePanelIds,
                context.FileMetadata,
                cancellationToken);
    }
}
