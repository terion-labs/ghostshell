using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private CapabilityRequestAwaiter? _capabilityRequestAwaiter;
    private bool _capabilityRequestDecisionConsumedThisTurn;

    public async ValueTask<GovernedAgentCapabilityDecisionResult>
        DecideCapabilityRequestAsync(
            AgentCapabilityRequestId requestId,
            GovernedAgentCapabilityDecision decision,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();

        CapabilityRequestAwaiter? awaiter;
        var expired = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            awaiter = _capabilityRequestAwaiter;
            if (awaiter is null || awaiter.Request.Id != requestId)
            {
                return CapabilityDecisionFailure(
                    "capability_request_not_found",
                    "That capability request is no longer pending.");
            }

            if (awaiter.DecisionStarted)
            {
                return CapabilityDecisionFailure(
                    "capability_request_decision_pending",
                    "A decision for that capability request is already being applied.");
            }

            // This is the one-way human-decision claim. Cancellation after
            // this point cannot make retrying the same request safe.
            cancellationToken.ThrowIfCancellationRequested();
            awaiter.DecisionStarted = true;
            _capabilityRequestDecisionConsumedThisTurn = true;
            expired = _timeProvider.GetUtcNow().ToUniversalTime()
                >= awaiter.Request.ExpiresAtUtc;
            _snapshot = _snapshot with
            {
                State = GovernedAgentState.StreamingProvider,
                PendingCapabilityRequest = null,
                Status = expired
                    ? "That capability request expired before the decision was accepted."
                    : decision is GovernedAgentCapabilityDecision.KeepOff
                        ? "Capability kept off; continuing the provider turn…"
                        : "Capability decision accepted; checking the run target and policy…",
            };
            awaiter.Decision.TrySetResult(expired ? null : decision);
        }

        NotifyChanged();
        if (expired)
        {
            return CapabilityDecisionFailure(
                "capability_request_expired",
                "That capability request expired.");
        }

        return await awaiter.Applied.Task.ConfigureAwait(false);
    }

    private async ValueTask<AgentToolResult> ExecuteCapabilityRequestAsync(
        AgentToolProposal proposal,
        ImmutableArray<AgentToolDefinition> advertisedTools,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var advertisedCandidates = GetCapabilityCandidates(advertisedTools);
        var parsed = AgentRequestCapabilityIntrinsic.Parse(
            proposal,
            advertisedCandidates
                .Select(candidate => candidate.Capability)
                .ToImmutableHashSet());
        if (parsed is AgentRequestCapabilityParseResult.Rejected rejected)
        {
            return CreateIntrinsicFailureResult(proposal, rejected.StableCode);
        }

        if (parsed is AgentRequestCapabilityParseResult.Unavailable)
        {
            return CreateIntrinsicFailureResult(
                proposal,
                "capability_request_unavailable");
        }

        var capability =
            ((AgentRequestCapabilityParseResult.Parsed)parsed).Capability;
        lock (_gate)
        {
            if (_capabilityRequestDecisionConsumedThisTurn)
            {
                return CreateIntrinsicFailureResult(
                    proposal,
                    "capability_request_limit_reached");
            }
        }

        var preflight = await InspectCapabilityCandidateAsync(
                capability,
                cancellationToken)
            .ConfigureAwait(false);
        if (preflight is CapabilityCandidateInspection.TargetChanged)
        {
            return CreateIntrinsicFailureResult(proposal, "target_changed");
        }

        if (preflight is not CapabilityCandidateInspection.Available available)
        {
            return CreateIntrinsicFailureResult(
                proposal,
                "capability_request_unavailable");
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        CapabilityRequestAwaiter awaiter;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_turnCancellation is null
                || _snapshot.State != GovernedAgentState.StreamingProvider)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (_capabilityRequestDecisionConsumedThisTurn)
            {
                return CreateIntrinsicFailureResult(
                    proposal,
                    "capability_request_limit_reached");
            }

            if (_capabilityRequestAwaiter is not null
                || _snapshot.YoloAuthority is not null
                || _effectivePolicy.GetPermission(capability)
                    != AgentPermission.Off
                || _runPolicy.GetPermission(capability)
                    != AgentPermission.Off)
            {
                return CreateIntrinsicFailureResult(
                    proposal,
                    "capability_request_unavailable");
            }

            var session = _session
                ?? throw new OperationCanceledException(cancellationToken);
            var target = _snapshot.Target
                ?? throw new OperationCanceledException(cancellationToken);
            var request = new GovernedAgentCapabilityRequest(
                AgentCapabilityRequestId.New(),
                session.RunId,
                capability,
                CapabilityDisplayTitle(capability),
                available.Candidate.AffectedToolTitles,
                target,
                available.TargetTitle,
                _policyGeneration,
                now + GovernedAgentCapabilityRequest.DecisionLifetime);
            awaiter = new CapabilityRequestAwaiter(
                request,
                available.Candidate);
            _capabilityRequestAwaiter = awaiter;
        }

        var requestAuditError = await _broker.RecordCapabilityRequestAuditAsync(
                new AgentCapabilityRequestAuditEvent.Requested(
                    awaiter.Request.Id,
                    awaiter.Request.RunId,
                    awaiter.Request.Capability,
                    awaiter.Request.Target,
                    awaiter.Request.PolicyGeneration),
                cancellationToken)
            .ConfigureAwait(false);
        if (requestAuditError is not null)
        {
            CancelCapabilityRequestAwaiter(
                awaiter,
                "audit_unavailable",
                "The capability request audit could not be persisted.");
            await QuarantineCapabilityPolicyFailureAsync("audit_unavailable")
                .ConfigureAwait(false);
            return CreateIntrinsicFailureResult(proposal, "audit_unavailable");
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_capabilityRequestAwaiter, awaiter)
                || _turnCancellation is null
                || _turnCancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            _snapshot = _snapshot with
            {
                State = GovernedAgentState.AwaitingCapabilityDecision,
                PendingCapabilityRequest = awaiter.Request,
                PendingApproval = null,
                PendingQuestion = null,
                ActiveTool = null,
                CurrentProgress = null,
                ProvisionalAssistantText = string.Empty,
                ProvisionalReasoningSummary = string.Empty,
                Status =
                    "Waiting for your run-local Off-to-Ask capability decision…",
            };
        }

        NotifyChanged();

        GovernedAgentCapabilityDecision? decision;
        try
        {
            decision = await AwaitCapabilityDecisionAsync(
                    awaiter,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            await RecordCapabilityTerminalAsync(
                    awaiter,
                    AgentCapabilityRequestAuditDecision.Cancelled,
                    GetOrCreateAgent(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            CancelCapabilityRequestAwaiter(
                awaiter,
                "capability_request_cancelled",
                "The capability request was cancelled.");
            throw;
        }

        if (decision is null)
        {
            var auditError = await RecordCapabilityTerminalAsync(
                    awaiter,
                    AgentCapabilityRequestAuditDecision.Expired,
                    GetOrCreateAgent(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (auditError is not null)
            {
                await QuarantineCapabilityPolicyFailureAsync("audit_unavailable")
                    .ConfigureAwait(false);
                return CreateIntrinsicFailureResult(proposal, "audit_unavailable");
            }

            CompleteCapabilityRequestAwaiter(
                awaiter,
                CapabilityDecisionFailure(
                    "capability_request_expired",
                    "That capability request expired."),
                "Capability request expired; returning that result to the provider…");
            return CreateIntrinsicFailureResult(
                proposal,
                "capability_request_expired");
        }

        var postflight = await InspectCapabilityCandidateAsync(
                capability,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (postflight is CapabilityCandidateInspection.TargetChanged)
        {
            var auditError = await RecordCapabilityTerminalAsync(
                    awaiter,
                    AgentCapabilityRequestAuditDecision.TargetChanged,
                    GetOrCreateAgent(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (auditError is not null)
            {
                await QuarantineCapabilityPolicyFailureAsync("audit_unavailable")
                    .ConfigureAwait(false);
                return CreateIntrinsicFailureResult(proposal, "audit_unavailable");
            }

            CompleteCapabilityRequestAwaiter(
                awaiter,
                CapabilityDecisionFailure(
                    "target_changed",
                    "The run target changed before the decision could be applied."),
                "The run target changed; the capability decision was discarded.");
            return CreateIntrinsicFailureResult(proposal, "target_changed");
        }

        if (postflight is not CapabilityCandidateInspection.Available
            {
                Candidate: var postflightCandidate,
            }
            || !CandidatesEqual(
                awaiter.Candidate,
                postflightCandidate))
        {
            var auditError = await RecordCapabilityTerminalAsync(
                    awaiter,
                    AgentCapabilityRequestAuditDecision.CapabilityUnavailable,
                    GetOrCreateAgent(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (auditError is not null)
            {
                await QuarantineCapabilityPolicyFailureAsync("audit_unavailable")
                    .ConfigureAwait(false);
                return CreateIntrinsicFailureResult(proposal, "audit_unavailable");
            }

            CompleteCapabilityRequestAwaiter(
                awaiter,
                CapabilityDecisionFailure(
                    "capability_request_unavailable",
                    "That capability is no longer available for this run."),
                "The capability is no longer available; the decision was discarded.");
            return CreateIntrinsicFailureResult(
                proposal,
                "capability_request_unavailable");
        }

        var policyChanged = false;
        lock (_gate)
        {
            if (_policyGeneration != awaiter.Request.PolicyGeneration)
            {
                policyChanged = true;
            }
        }

        if (policyChanged)
        {
            var auditError = await RecordCapabilityTerminalAsync(
                    awaiter,
                    AgentCapabilityRequestAuditDecision.PolicyChanged,
                    GetOrCreateAgent(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (auditError is not null)
            {
                await QuarantineCapabilityPolicyFailureAsync("audit_unavailable")
                    .ConfigureAwait(false);
                return CreateIntrinsicFailureResult(proposal, "audit_unavailable");
            }

            CompleteCapabilityRequestAwaiter(
                awaiter,
                CapabilityDecisionFailure(
                    "policy_changed",
                    "The run policy changed before the decision could be applied."),
                "The run policy changed; the capability decision was discarded.");
            return CreateIntrinsicFailureResult(proposal, "policy_changed");
        }

        if (decision is GovernedAgentCapabilityDecision.KeepOff)
        {
            var auditError = await RecordCapabilityTerminalAsync(
                    awaiter,
                    AgentCapabilityRequestAuditDecision.Denied,
                    _approvalActor,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (auditError is not null)
            {
                await QuarantineCapabilityPolicyFailureAsync("audit_unavailable")
                    .ConfigureAwait(false);
                return CreateIntrinsicFailureResult(proposal, "audit_unavailable");
            }

            CompleteCapabilityRequestAwaiter(
                awaiter,
                new GovernedAgentCapabilityDecisionResult(
                    true,
                    "capability_request_denied",
                    "The capability remains off."),
                "Capability kept off; returning that result to the provider…");
            return CreateIntrinsicFailureResult(
                proposal,
                "capability_request_denied");
        }

        var updateError = await ApplyCapabilityGrantAsync(
                awaiter,
                cancellationToken)
            .ConfigureAwait(false);
        if (updateError is not null)
        {
            await RecordCapabilityTerminalAsync(
                    awaiter,
                    updateError.Code == AgentAuthorizationErrorCode.RunCancelled
                        ? AgentCapabilityRequestAuditDecision.Cancelled
                        : updateError.Code == AgentAuthorizationErrorCode.AuditUnavailable
                            ? AgentCapabilityRequestAuditDecision.AuditFailed
                            : AgentCapabilityRequestAuditDecision.PolicyChanged,
                    GetOrCreateAgent(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            var stableCode = StableCode(updateError.Code);
            await QuarantineCapabilityPolicyFailureAsync(
                    stableCode)
                .ConfigureAwait(false);
            CompleteCapabilityRequestAwaiter(
                awaiter,
                CapabilityDecisionFailure(
                    stableCode,
                    "The capability policy update could not be confirmed."),
                "The capability update failed closed; the run was stopped.");
            return CreateIntrinsicFailureResult(proposal, stableCode);
        }

        var allowedAuditError = await RecordCapabilityTerminalAsync(
                awaiter,
                AgentCapabilityRequestAuditDecision.Allowed,
                _approvalActor,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (allowedAuditError is not null)
        {
            await QuarantineCapabilityPolicyFailureAsync("audit_unavailable")
                .ConfigureAwait(false);
            CompleteCapabilityRequestAwaiter(
                awaiter,
                CapabilityDecisionFailure(
                    "audit_unavailable",
                    "The capability decision audit could not be confirmed."),
                "The capability update failed closed; the run was stopped.");
            return CreateIntrinsicFailureResult(proposal, "audit_unavailable");
        }

        CompleteCapabilityRequestAwaiter(
            awaiter,
            new GovernedAgentCapabilityDecisionResult(
                true,
                "capability_request_allowed",
                "The capability now uses ordinary per-action approval for this run."),
            "Capability set to Ask; returning the run-local receipt to the provider…");
        return new AgentToolResult(
            proposal,
            AgentToolResultStatus.Succeeded,
            "tool_succeeded",
            JsonValue(CapabilityGrantReceipt(capability)));
    }

    private async ValueTask<CapabilityCandidateInspection>
        InspectCapabilityCandidateAsync(
            AgentCapability capability,
            CancellationToken cancellationToken)
    {
        var contexts = await InspectRunTargetContextAsync(
                GetPinnedTarget(),
                GetOrCreateAgent(),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (contexts is null || !MatchesPinnedScope(contexts))
        {
            return new CapabilityCandidateInspection.TargetChanged();
        }

        var context = contexts;

        var resizeAttachments = await InspectResizeAttachmentsAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var browserEligiblePanelIds = await InspectBrowserAttachmentsAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var fileMetadata = await InspectFileSessionsAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var tools = BuildAgentTools(
            context,
            resizeAttachments.Keys.ToImmutableHashSet(),
            browserEligiblePanelIds,
            fileMetadata);
        var candidate = GetCapabilityCandidates(tools)
            .SingleOrDefault(candidate =>
                candidate.Capability == capability);
        return candidate is null
            ? new CapabilityCandidateInspection.Unavailable()
            : new CapabilityCandidateInspection.Available(
                candidate,
                TrustedCapabilityTargetTitle(context));
    }

    private async ValueTask<AgentAuthorizationError?>
        ApplyCapabilityGrantAsync(
            CapabilityRequestAwaiter awaiter,
            CancellationToken cancellationToken)
    {
        AgentRunPolicyUpdate update;
        CancellationTokenSource turnCancellation;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(_capabilityRequestAwaiter, awaiter)
                || !_runRegistered
                || _session?.RunId != awaiter.Request.RunId
                || _policyGeneration != awaiter.Request.PolicyGeneration)
            {
                return new AgentAuthorizationError(
                    AgentAuthorizationErrorCode.PolicyChanged,
                    "The run policy changed before the capability grant.");
            }

            if (_snapshot.YoloAuthority is not null
                || _runPolicy.GetPermission(awaiter.Request.Capability)
                    != AgentPermission.Off
                || _effectivePolicy.GetPermission(awaiter.Request.Capability)
                    != AgentPermission.Off)
            {
                return new AgentAuthorizationError(
                    AgentAuthorizationErrorCode.PolicyChanged,
                    "The capability is no longer disabled.");
            }

            turnCancellation = _turnCancellation
                ?? throw new OperationCanceledException(cancellationToken);
            var nextGeneration = checked(_policyGeneration + 1);
            var permissions = AgentPolicy.Capabilities.ToImmutableDictionary(
                capability => capability,
                capability => capability == awaiter.Request.Capability
                    ? AgentPermission.Ask
                    : _runPolicy.GetPermission(capability));
            var nextRunPolicy = new AgentPolicy(
                _runPolicy.Provider,
                _runPolicy.Model,
                permissions)
            {
                CompactionModel = _runPolicy.CompactionModel,
                TitleModel = _runPolicy.TitleModel,
                SystemPrompt = _runPolicy.SystemPrompt,
            };
            update = new AgentRunPolicyUpdate(
                awaiter.Request.RunId,
                nextRunPolicy,
                nextGeneration,
                _approvalActor,
                capabilityRequestId: awaiter.Request.Id);
            _policyChangeInFlight = true;
        }

        AgentAuthorizationError? error;
        try
        {
            error = await _broker
                .UpdateRunPolicyAsync(update, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                _policyChangeInFlight = false;
            }

            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            error = new AgentAuthorizationError(
                AgentAuthorizationErrorCode.AuditUnavailable,
                "The capability policy update could not be confirmed.");
        }

        if (error is not null)
        {
            lock (_gate)
            {
                _policyChangeInFlight = false;
            }

            return error;
        }

        lock (_gate)
        {
            if (_disposed
                || !_runRegistered
                || _session?.RunId != update.RunId
                || !ReferenceEquals(_turnCancellation, turnCancellation)
                || turnCancellation.IsCancellationRequested
                || _snapshot.State is
                    GovernedAgentState.Cancelling
                    or GovernedAgentState.Cancelled)
            {
                _policyChangeInFlight = false;
                return new AgentAuthorizationError(
                    AgentAuthorizationErrorCode.RunCancelled,
                    "The run stopped before the capability grant became visible.");
            }

            _runPolicy = update.Policy;
            _effectivePolicy = update.Policy;
            _policyGeneration = update.PolicyGeneration;
            _policyChangeInFlight = false;
            _snapshot = _snapshot with
            {
                TerminalMutationPermission =
                    update.Policy.GetPermission(AgentCapability.RunCommands),
                EffectivePolicy = update.Policy,
                BaselinePolicy = _baselinePolicy,
                RunPolicy = _runPolicy,
                PolicyGeneration = _policyGeneration,
                Status =
                    "Capability set to Ask for this run; later actions still require approval.",
            };
        }

        NotifyChanged();
        return null;
    }

    private async ValueTask QuarantineCapabilityPolicyFailureAsync(
        string stableCode)
    {
        var revocationError = await CancelRegisteredRunBestEffortAsync(
                stableCode,
                CancellationToken.None)
            .ConfigureAwait(false);
        CancellationTokenSource? turnCancellation;
        NativeAgentSession? session;
        ApprovalAwaiter? approval;
        QuestionAwaiter? question;
        lock (_gate)
        {
            _policyChangeInFlight = false;
            DisposeYoloExpiryTimerUnsafe();
            _runPolicy = _baselinePolicy;
            _effectivePolicy = _baselinePolicy;
            turnCancellation = _turnCancellation;
            session = _session;
            approval = _approvalAwaiter;
            _approvalAwaiter = null;
            question = DetachQuestionAwaiterUnsafe();
            _snapshot = _snapshot with
            {
                State = GovernedAgentState.Cancelled,
                PendingApproval = null,
                PendingQuestion = null,
                PendingCapabilityRequest = null,
                ActiveTool = null,
                ProvisionalAssistantText = string.Empty,
                ProvisionalReasoningSummary = string.Empty,
                CurrentProgress = null,
                TerminalMutationPermission =
                    _baselinePolicy.GetPermission(AgentCapability.RunCommands),
                EffectivePolicy = _baselinePolicy,
                BaselinePolicy = _baselinePolicy,
                RunPolicy = _runPolicy,
                PolicyGeneration = _policyGeneration,
                YoloAuthority = null,
                Status = revocationError is null
                    ? "The capability policy update failed closed. The run was quarantined and its authority was revoked."
                    : "The capability policy update failed closed. Clear the quarantined run before continuing.",
            };
        }

        TryCancel(turnCancellation);
        session?.Cancel();
        approval?.Completion.TrySetCanceled();
        CancelDetachedQuestionAwaiter(
            question,
            "question_cancelled",
            "The agent question was cancelled.");
        NotifyChanged();
    }

    private async ValueTask<AgentAuthorizationError?>
        RecordCapabilityTerminalAsync(
            CapabilityRequestAwaiter awaiter,
            AgentCapabilityRequestAuditDecision decision,
            ActorDescriptor actor,
            CancellationToken cancellationToken)
    {
        try
        {
            return await _broker.RecordCapabilityRequestAuditAsync(
                    new AgentCapabilityRequestAuditEvent.Terminal(
                        awaiter.Request.Id,
                        awaiter.Request.RunId,
                        decision,
                        actor),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return new AgentAuthorizationError(
                AgentAuthorizationErrorCode.AuditUnavailable,
                "The capability request audit could not be confirmed.");
        }
    }

    private ImmutableArray<AgentToolDefinition>
        RefreshCapabilityRequestTool(
            ImmutableArray<AgentToolDefinition> tools)
    {
        var productionAndOtherIntrinsics = tools
            .Where(tool => !string.Equals(
                tool.Name,
                IntrinsicAgentTools.RequestCapability,
                StringComparison.Ordinal))
            .ToImmutableArray();
        var candidates = GetCapabilityCandidates(
            productionAndOtherIntrinsics);
        return candidates.IsDefaultOrEmpty
            ? productionAndOtherIntrinsics
            : productionAndOtherIntrinsics.Add(
                AgentRequestCapabilityIntrinsic.CreateDefinition(
                    [.. candidates.Select(candidate => candidate.Capability)]));
    }

    private ImmutableArray<CapabilityCandidate> GetCapabilityCandidates(
        ImmutableArray<AgentToolDefinition> tools)
    {
        AgentPolicy runPolicy;
        AgentPolicy effectivePolicy;
        lock (_gate)
        {
            if (_snapshot.YoloAuthority is not null)
            {
                return [];
            }

            runPolicy = _runPolicy;
            effectivePolicy = _effectivePolicy;
        }

        return [.. tools
            .Select(tool => _toolCatalog.TryGet(
                    tool.Name,
                    out var descriptor)
                ? descriptor
                : null)
            .OfType<AgentToolDescriptor>()
            .Where(descriptor =>
                runPolicy.GetPermission(descriptor.Capability)
                    == AgentPermission.Off
                &&
                effectivePolicy.GetPermission(descriptor.Capability)
                    == AgentPermission.Off)
            .GroupBy(descriptor => descriptor.Capability)
            .Select(group => new CapabilityCandidate(
                group.Key,
                [.. group.OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal).Select(descriptor => descriptor.Name)],
                [.. group.OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
                    .Select(descriptor => descriptor.Title)
                    .Distinct(StringComparer.Ordinal)]))
            .OrderBy(
                candidate => AgentCapabilityProtocol.GetToken(
                    candidate.Capability),
                StringComparer.Ordinal)];
    }

    private async ValueTask<GovernedAgentCapabilityDecision?>
        AwaitCapabilityDecisionAsync(
            CapabilityRequestAwaiter awaiter,
            CancellationToken cancellationToken)
    {
        var remaining = awaiter.Request.ExpiresAtUtc
            - _timeProvider.GetUtcNow().ToUniversalTime();
        if (remaining <= TimeSpan.Zero)
        {
            ExpireCapabilityRequestAwaiter(awaiter);
            return null;
        }

        try
        {
            return await awaiter.Decision.Task
                .WaitAsync(
                    remaining,
                    _timeProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ExpireCapabilityRequestAwaiter(awaiter);
            return await awaiter.Decision.Task.ConfigureAwait(false);
        }
    }

    private void ExpireCapabilityRequestAwaiter(
        CapabilityRequestAwaiter awaiter)
    {
        var notify = false;
        lock (_gate)
        {
            if (ReferenceEquals(_capabilityRequestAwaiter, awaiter)
                && !awaiter.DecisionStarted)
            {
                awaiter.DecisionStarted = true;
                _capabilityRequestDecisionConsumedThisTurn = true;
                awaiter.Decision.TrySetResult(null);
                _snapshot = _snapshot with
                {
                    State = GovernedAgentState.StreamingProvider,
                    PendingCapabilityRequest = null,
                    Status =
                        "Capability request expired; returning that result to the provider…",
                };
                notify = true;
            }
        }

        if (notify)
        {
            NotifyChanged();
        }
    }

    private void CompleteCapabilityRequestAwaiter(
        CapabilityRequestAwaiter awaiter,
        GovernedAgentCapabilityDecisionResult result,
        string status)
    {
        lock (_gate)
        {
            CompleteCapabilityRequestAwaiterUnsafe(
                awaiter,
                result,
                status);
        }

        NotifyChanged();
    }

    private void CompleteCapabilityRequestAwaiterUnsafe(
        CapabilityRequestAwaiter awaiter,
        GovernedAgentCapabilityDecisionResult result,
        string status)
    {
        if (ReferenceEquals(_capabilityRequestAwaiter, awaiter))
        {
            _capabilityRequestAwaiter = null;
            _snapshot = _snapshot with
            {
                State = _snapshot.State == GovernedAgentState.Cancelled
                    ? GovernedAgentState.Cancelled
                    : GovernedAgentState.StreamingProvider,
                PendingCapabilityRequest = null,
                Status = _snapshot.State == GovernedAgentState.Cancelled
                    ? _snapshot.Status
                    : status,
            };
        }

        awaiter.Applied.TrySetResult(result);
    }

    private void CancelCapabilityRequestAwaiter(
        CapabilityRequestAwaiter awaiter,
        string stableCode,
        string message)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_capabilityRequestAwaiter, awaiter))
            {
                _capabilityRequestAwaiter = null;
                _snapshot = _snapshot with
                {
                    PendingCapabilityRequest = null,
                };
            }
        }

        awaiter.Decision.TrySetCanceled();
        awaiter.Applied.TrySetResult(
            CapabilityDecisionFailure(stableCode, message));
    }

    private CapabilityRequestAwaiter? DetachCapabilityRequestAwaiterUnsafe()
    {
        var awaiter = _capabilityRequestAwaiter;
        _capabilityRequestAwaiter = null;
        return awaiter;
    }

    private static void CancelDetachedCapabilityRequestAwaiter(
        CapabilityRequestAwaiter? awaiter,
        string stableCode,
        string message)
    {
        if (awaiter is null)
        {
            return;
        }

        awaiter.Decision.TrySetCanceled();
        awaiter.Applied.TrySetResult(
            CapabilityDecisionFailure(stableCode, message));
    }

    private async ValueTask AuditDetachedCapabilityRequestCancellationAsync(
        CapabilityRequestAwaiter? awaiter)
    {
        if (awaiter is null || awaiter.DecisionStarted)
        {
            return;
        }

        await RecordCapabilityTerminalAsync(
                awaiter,
                AgentCapabilityRequestAuditDecision.Cancelled,
                GetOrCreateAgent(),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static GovernedAgentCapabilityDecisionResult
        CapabilityDecisionFailure(
            string stableCode,
            string message) =>
        new(false, stableCode, message);

    private static string CapabilityDisplayTitle(
        AgentCapability capability) =>
        capability switch
        {
            AgentCapability.TerminalRead => "Terminal reading",
            AgentCapability.RunCommands => "Terminal commands",
            AgentCapability.EditFiles => "File changes",
            AgentCapability.ReadFiles => "File reading",
            AgentCapability.Search => "Workspace search",
            AgentCapability.Git => "Git changes",
            AgentCapability.WebFetch => "Web access",
            AgentCapability.Docker => "Docker control",
            AgentCapability.DestructiveTerminalActions =>
                "Destructive terminal actions",
            AgentCapability.BrowserNavigation => "Browser navigation",
            AgentCapability.BrowserData => "Browser data",
            AgentCapability.ProcessControl => "Process control",
            AgentCapability.McpTools => "MCP tools",
            AgentCapability.SecretUse => "Secret use",
            AgentCapability.BrowserInteraction => "Browser interaction",
            AgentCapability.BrowserScripting => "Browser scripting",
            AgentCapability.BrowserDiagnostics => "Browser diagnostics",
            AgentCapability.DatabaseRead => "Database reading",
            AgentCapability.DatabaseWrite => "Database changes",
            AgentCapability.DockerData => "Docker inspection",
            AgentCapability.SystemData => "System statistics",
            AgentCapability.ProcessData => "Process inspection",
            AgentCapability.ArtifactTransfer => "Artifact transfers",
            AgentCapability.WorkspaceLayout => "Workspace layout",
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };

    private static string TrustedCapabilityTargetTitle(
        AgentContextSnapshot context)
    {
        var firstKind = context.Panels[0].Kind;
        return context.Target switch
        {
            AgentTarget.Panel or AgentTarget.ConnectionSession =>
                firstKind switch
                {
                    PanelKind.Terminal => "Terminal",
                    PanelKind.Browser => "Browser",
                    PanelKind.FileViewer => "File Viewer",
                    PanelKind.ProcessMonitor => "Process Monitor",
                    _ => "Panel",
                },
            AgentTarget.OpenTab =>
                $"Current tab · {context.Panels.Count} panels",
            AgentTarget.Workspace =>
                $"Workspace · {context.Panels.Count} panels",
            AgentTarget.SelectedPanels =>
                $"Selected panels · {context.Panels.Count}",
            _ => throw new ArgumentOutOfRangeException(
                nameof(context),
                context.Target.GetType(),
                "The agent target kind is unsupported."),
        };
    }

    private static bool CandidatesEqual(
        CapabilityCandidate left,
        CapabilityCandidate right) =>
        left.Capability == right.Capability
        && left.ToolNames.SequenceEqual(
            right.ToolNames,
            StringComparer.Ordinal)
        && left.AffectedToolTitles.SequenceEqual(
            right.AffectedToolTitles,
            StringComparer.Ordinal);

    private static string CapabilityGrantReceipt(
        AgentCapability capability)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        writer.WriteString(
            "capability",
            AgentCapabilityProtocol.GetToken(capability));
        writer.WriteString("permission", "ask");
        writer.WriteString("scope", "run");
        writer.WriteBoolean("action_approval_required", true);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed record CapabilityCandidate(
        AgentCapability Capability,
        ImmutableArray<string> ToolNames,
        ImmutableArray<string> AffectedToolTitles);

    private abstract record CapabilityCandidateInspection
    {
        private CapabilityCandidateInspection()
        {
        }

        public sealed record Available(
            CapabilityCandidate Candidate,
            string TargetTitle)
            : CapabilityCandidateInspection;

        public sealed record Unavailable : CapabilityCandidateInspection;

        public sealed record TargetChanged : CapabilityCandidateInspection;
    }

    private sealed class CapabilityRequestAwaiter(
        GovernedAgentCapabilityRequest request,
        CapabilityCandidate candidate)
    {
        public GovernedAgentCapabilityRequest Request { get; } = request;

        public CapabilityCandidate Candidate { get; } = candidate;

        public TaskCompletionSource<GovernedAgentCapabilityDecision?> Decision
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<GovernedAgentCapabilityDecisionResult>
            Applied
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DecisionStarted { get; set; }
    }
}
