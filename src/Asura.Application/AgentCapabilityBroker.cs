using System.Collections.Concurrent;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Converts inert model proposals into, at most, one short-lived authorization.
/// The broker owns live run policy and cancellation state. It never executes a
/// tool; a typed session-host operation must consume the authorization
/// immediately before execution.
/// </summary>
public sealed class AgentCapabilityBroker :
    IAgentCapabilityBroker,
    IAgentMcpRunAuthorityVerifier,
    IAsyncDisposable
{
    public static readonly TimeSpan DefaultApprovalLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultAuthorizationLifetime = TimeSpan.FromSeconds(30);

    private const int MaximumRunCount = 128;
    private const int MaximumClaimCount = 8192;
    private const int MaximumPendingApprovalCount = 512;
    private const int MaximumAuthorizationCount = 512;
    private const int MaximumActiveActionCount = 128;
    private const int MaximumCancelledRunCount = 8192;
    private const string AuditTargetKind = "agent-target-fingerprint";
    private const string PolicyAuditAction = "agent.run.policy";
    private const string CapabilityRequestAuditAction = "agent.capability.request";

    private readonly AgentToolCatalog _tools;
    private readonly IAuditStore _auditStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<AgentRunId, RunAuthority> _runs = [];
    private readonly Dictionary<AgentApprovalId, PendingApproval> _pendingApprovals = [];
    private readonly Dictionary<AgentAuthorizationId, IssuedAuthorization> _authorizations = [];
    private readonly Dictionary<AgentAuthorizationId, ActiveAction> _activeActions = [];
    private readonly Dictionary<AgentAuthorizationId, PendingCompletionAudit>
        _pendingCompletionAudits = [];
    private readonly Dictionary<AgentActionId, AgentRunId> _claimedActions = [];
    private readonly ConcurrentDictionary<AgentRunId, RunAuthoritySignal>
        _runAuthoritySignals = [];
    private readonly ConcurrentDictionary<AgentRunId, byte> _cancelledRuns = [];
    private int _disposeStarted;
    private bool _disposed;

    public AgentCapabilityBroker(
        AgentToolCatalog tools,
        IAuditStore auditStore,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(auditStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _tools = tools;
        _auditStore = auditStore;
        _timeProvider = timeProvider;
    }

    public async ValueTask<AgentAuthorizationError?> RegisterRunAsync(
        AgentRunRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return Cancelled();
        }

        try
        {
            if (_disposed)
            {
                return BrokerDisposed();
            }

            if (_runs.ContainsKey(registration.RunId)
                || _cancelledRuns.ContainsKey(registration.RunId))
            {
                return Error(
                    AgentAuthorizationErrorCode.RunAlreadyRegistered,
                    "The agent run already has live authorization state.");
            }

            if (_runs.Count >= MaximumRunCount
                || _cancelledRuns.Count >= MaximumCancelledRunCount)
            {
                return CapacityExceeded("agent runs");
            }

            var now = _timeProvider.GetUtcNow();
            var yoloError = ValidateYoloConfirmation(
                registration.Policy,
                registration.YoloConfirmation,
                registration.RunId,
                registration.Target,
                registration.PolicyGeneration,
                now,
                registration.ApprovingClientId);
            if (yoloError is not null)
            {
                return yoloError;
            }

            var authoritySignal = new RunAuthoritySignal(
                registration.Agent.Id,
                registration.ApprovingClientId,
                registration.PolicyGeneration);
            _runs.Add(
                registration.RunId,
                new RunAuthority(
                    registration.RunId,
                    registration.Agent,
                    registration.ApprovingClientId,
                    registration.Target,
                    registration.Policy,
                    registration.PolicyGeneration,
                    registration.YoloConfirmation));
            if (!_runAuthoritySignals.TryAdd(
                    registration.RunId,
                    authoritySignal))
            {
                _runs.Remove(registration.RunId);
                return Error(
                    AgentAuthorizationErrorCode.RunAlreadyRegistered,
                    "The agent run already has live authority signals.");
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AgentMcpRunAuthorityResult> AcquireAsync(
        AgentMcpRunAuthorityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return McpRunDenied(Cancelled());
        }

        try
        {
            if (_disposed)
            {
                return McpRunDenied(BrokerDisposed());
            }

            if (!_runs.TryGetValue(request.RunId, out var run))
            {
                return McpRunDenied(
                    _cancelledRuns.ContainsKey(request.RunId)
                        ? RunCancelled()
                        : RunNotFound());
            }

            if (request.Agent != run.Agent)
            {
                return McpRunDenied(
                    Error(
                        AgentAuthorizationErrorCode.RunActorMismatch,
                        "The MCP launch actor does not own the live agent run."));
            }

            if (run.Cancelled)
            {
                return McpRunDenied(RunCancelled());
            }

            if (run.Suspended)
            {
                return McpRunDenied(RunSuspended());
            }

            var mcpPermission =
                run.Policy.GetPermission(AgentCapability.McpTools);
            if (mcpPermission is not (
                    AgentPermission.Ask
                    or AgentPermission.Auto
                    or AgentPermission.Yolo))
            {
                return McpRunDenied(
                    Error(
                        AgentAuthorizationErrorCode.PolicyDenied,
                        "The live run policy does not permit MCP tools."));
            }

            if (mcpPermission == AgentPermission.Yolo
                && !HasActiveYoloConfirmation(run, _timeProvider.GetUtcNow()))
            {
                return McpRunDenied(
                    Error(
                        AgentAuthorizationErrorCode.PolicyDenied,
                        "MCP launch requires current full-access authority."));
            }

            if (!_runAuthoritySignals.TryGetValue(
                    request.RunId,
                    out var authoritySignal)
                || !authoritySignal.TryCaptureGenerationToken(
                    run.PolicyGeneration,
                    out var revocationToken))
            {
                return McpRunDenied(
                    _cancelledRuns.ContainsKey(request.RunId)
                        ? RunCancelled()
                        : RunSuspended());
            }

            return new AgentMcpRunAuthorityResult.Granted(
                new AgentMcpRunAuthorityLease(
                    run.RunId,
                    run.Agent,
                    run.PolicyGeneration,
                    revocationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AgentAuthorizationError?> UpdateRunPolicyAsync(
        AgentRunPolicyUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var policyApplied = false;
        var signalError = BeginRunPolicyUpdate(
            update,
            out RunAuthoritySignal? authoritySignal,
            out var revokedGeneration,
            out PolicyUpdateLease? policyUpdateLease);
        if (signalError is not null)
        {
            return signalError;
        }

        if (authoritySignal is not null)
        {
            if (revokedGeneration is not null)
            {
                BeginCancellationAndDispose(revokedGeneration);
            }
        }

        List<CancellationTokenSource> cancellations = [];
        AgentAuthorizationError? result;
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (policyUpdateLease is not null)
            {
                authoritySignal!.AbortPolicyUpdate(policyUpdateLease);
            }

            return Cancelled();
        }

        try
        {
            if (_disposed)
            {
                return BrokerDisposed();
            }

            if (!_runs.TryGetValue(update.RunId, out var run))
            {
                return _cancelledRuns.ContainsKey(update.RunId)
                    ? RunCancelled()
                    : RunNotFound();
            }

            if (authoritySignal is null)
            {
                if (!_runAuthoritySignals.TryGetValue(update.RunId, out authoritySignal))
                {
                    return RunCancelled();
                }

                signalError = BeginRunPolicyUpdate(
                    update,
                    authoritySignal,
                    out revokedGeneration,
                    out policyUpdateLease);
                if (signalError is not null)
                {
                    return signalError;
                }

                if (revokedGeneration is not null)
                {
                    BeginCancellationAndDispose(revokedGeneration);
                }
            }

            if (run.Cancelled)
            {
                return RunCancelled();
            }

            if (update.ChangedBy.ClientId != run.ApprovingClientId)
            {
                return Error(
                    AgentAuthorizationErrorCode.ApprovalActorMismatch,
                    "The policy change came from a different desktop client.");
            }

            var retryingSuspendedUpdate = run.PendingPolicyAuditEvent is not null
                && update.PolicyGeneration == run.PolicyGeneration
                && update.Policy == run.Policy
                && update.YoloConfirmation == run.YoloConfirmation
                && run.PendingPolicyAuditEvent.Details
                    is AuditDetails.AgentRunPolicyTransitionDetails pendingPolicyDetails
                && pendingPolicyDetails.CapabilityRequestId
                    == update.CapabilityRequestId;
            if (!retryingSuspendedUpdate)
            {
                var capabilityUpdateError = ValidateCapabilityPolicyUpdate(run, update);
                if (capabilityUpdateError is not null)
                {
                    return capabilityUpdateError;
                }
            }
            if (run.Suspended && !retryingSuspendedUpdate)
            {
                return RunSuspended();
            }

            if (!retryingSuspendedUpdate
                && update.PolicyGeneration <= run.PolicyGeneration)
            {
                return Error(
                    AgentAuthorizationErrorCode.PolicyChanged,
                    "A run policy update must advance the authoritative generation.");
            }

            var now = _timeProvider.GetUtcNow();
            var yoloError = ValidateYoloConfirmation(
                update.Policy,
                update.YoloConfirmation,
                run.RunId,
                run.Target,
                update.PolicyGeneration,
                now,
                run.ApprovingClientId);
            if (yoloError is not null)
            {
                return yoloError;
            }

            var policyAuditEvent = retryingSuspendedUpdate
                ? run.PendingPolicyAuditEvent
                    ?? throw new InvalidOperationException(
                        "A suspended policy update is missing its durable audit event.")
                : CreatePolicyTransitionAuditEvent(run, update, now);

            // This is the revocation linearization point. The run becomes
            // unusable before any old approval or token is audited/removed.
            run.Policy = update.Policy;
            run.PolicyGeneration = update.PolicyGeneration;
            run.YoloConfirmation = update.YoloConfirmation;
            run.PendingPolicyAuditEvent = policyAuditEvent;
            run.Suspended = true;
            policyApplied = true;
            cancellations.AddRange(CollectActiveCancellations(run.RunId));
            SignalActiveActions(cancellations);

            var revocation = new Revocation(
                Error(
                    AgentAuthorizationErrorCode.PolicyChanged,
                    "The run policy changed before the action started."),
                update.ChangedBy,
                now);
            result = await RevokeInactiveActionsAsync(
                    run.RunId,
                    revocation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is null)
            {
                result = await AppendPolicyTransitionAuditAsync(
                        policyAuditEvent,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    run.PendingPolicyAuditEvent = null;
                    run.Suspended = _pendingCompletionAudits.Values.Any(
                        pending => pending.RunId == run.RunId);
                    authoritySignal.CompletePolicyUpdate(
                        policyUpdateLease
                        ?? throw new InvalidOperationException(
                            "A policy update completed without an authority lease."));
                }
            }
        }
        finally
        {
            if (policyUpdateLease is not null && !policyApplied)
            {
                authoritySignal!.AbortPolicyUpdate(policyUpdateLease);
            }

            _gate.Release();
        }

        await CancelActiveActionsAsync(cancellations).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<AgentAuthorizationError?> RecordCapabilityRequestAuditAsync(
        AgentCapabilityRequestAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return Cancelled();
        }

        try
        {
            if (_disposed)
            {
                return BrokerDisposed();
            }

            var runId = auditEvent switch
            {
                AgentCapabilityRequestAuditEvent.Requested requested => requested.RunId,
                AgentCapabilityRequestAuditEvent.Terminal terminal => terminal.RunId,
                _ => throw new ArgumentOutOfRangeException(nameof(auditEvent)),
            };
            if (!_runs.TryGetValue(runId, out var run))
            {
                return _cancelledRuns.ContainsKey(runId)
                    ? RunCancelled()
                    : RunNotFound();
            }

            return auditEvent switch
            {
                AgentCapabilityRequestAuditEvent.Requested requested =>
                    await RecordCapabilityRequestedAsync(
                            run,
                            requested,
                            cancellationToken)
                        .ConfigureAwait(false),
                AgentCapabilityRequestAuditEvent.Terminal terminal =>
                    await RecordCapabilityTerminalAsync(
                            run,
                            terminal,
                            cancellationToken)
                        .ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(auditEvent)),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AgentAuthorizationError?> CancelRunAsync(
        AgentRunCancellation cancellation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        SignalRunCancellation(cancellation);
        List<CancellationTokenSource> cancellations = [];
        AgentAuthorizationError? result;
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return Cancelled();
        }

        try
        {
            if (_disposed)
            {
                return BrokerDisposed();
            }

            if (!_runs.TryGetValue(cancellation.RunId, out var run))
            {
                return _cancelledRuns.ContainsKey(cancellation.RunId)
                    ? null
                    : RunNotFound();
            }

            if (!CanCancelRun(
                    cancellation.Actor,
                    run.Agent.Id,
                    run.ApprovingClientId))
            {
                return Error(
                    AgentAuthorizationErrorCode.RunActorMismatch,
                    "The cancellation actor does not own this agent run.");
            }

            if (run.PendingCapabilityRequest is not null)
            {
                run.Suspended = true;
                return Error(
                    AgentAuthorizationErrorCode.AuditUnavailable,
                    "The run remains quarantined until its capability-request audit is closed.");
            }

            var now = _timeProvider.GetUtcNow();
            run.Cancelled = true;
            run.Suspended = true;
            _cancelledRuns.TryAdd(run.RunId, 0);
            SignalRunCancellation(cancellation);
            cancellations.AddRange(CollectActiveCancellations(run.RunId));
            SignalActiveActions(cancellations);
            var revocation = new Revocation(
                Error(
                    AgentAuthorizationErrorCode.RunCancelled,
                    "The agent run was cancelled before the action started."),
                cancellation.Actor,
                now);
            result = await RevokeInactiveActionsAsync(
                    run.RunId,
                    revocation,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                foreach (var actionId in _claimedActions
                    .Where(entry => entry.Value == run.RunId)
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    _claimedActions.Remove(actionId);
                }

                _runs.Remove(run.RunId);
            }
        }
        finally
        {
            _gate.Release();
        }

        await CancelActiveActionsAsync(cancellations).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<AgentAuthorizationResult> RequestAsync(
        AgentActionProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return Denied(Cancelled());
        }

        try
        {
            if (_disposed)
            {
                return Denied(BrokerDisposed());
            }

            var now = _timeProvider.GetUtcNow();
            var sweepError = await SweepExpiredInactiveActionsAsync(
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (sweepError is not null)
            {
                return Denied(sweepError);
            }

            if (_claimedActions.ContainsKey(proposal.Id))
            {
                return await DenyDuplicateAsync(proposal, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!_tools.TryGet(proposal.ToolName, out var tool) || tool is null)
            {
                return await DenyUntrustedAsync(
                        proposal,
                        Error(
                            AgentAuthorizationErrorCode.UnknownTool,
                            "The requested tool is not in the trusted tool catalog."),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!_runs.TryGetValue(proposal.RunId, out var run))
            {
                return await DenyUntrustedAsync(
                        proposal,
                        _cancelledRuns.ContainsKey(proposal.RunId)
                            ? RunCancelled()
                            : RunNotFound(),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var runError = ValidateProposalAgainstRun(proposal, run);
            var permission = run.Policy.GetPermission(tool.Capability);
            var decision = AgentPolicyResolver.Evaluate(permission, tool.Risk);
            var requestedAuditError = await AppendKnownAuditAsync(
                    proposal,
                    tool,
                    permission,
                    decision,
                    AuditOutcome.Requested,
                    run.Agent,
                    authorizationSource: null,
                    errorCode: null,
                    resultCode: null,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (requestedAuditError is not null)
            {
                return Denied(requestedAuditError);
            }

            if (_claimedActions.Count < MaximumClaimCount)
            {
                _claimedActions.Add(proposal.Id, proposal.RunId);
            }

            runError ??= ValidateProposalAgainstRun(proposal, run);
            if (runError is not null)
            {
                return await DenyKnownAsync(
                        proposal,
                        tool,
                        permission,
                        decision,
                        runError,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_claimedActions.Count >= MaximumClaimCount
                && !_claimedActions.ContainsKey(proposal.Id))
            {
                return await DenyKnownAsync(
                        proposal,
                        tool,
                        permission,
                        decision,
                        CapacityExceeded("action claims"),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (proposal.CreatedAtUtc > now
                || proposal.DeadlineUtc <= now)
            {
                return await DenyKnownAsync(
                        proposal,
                        tool,
                        permission,
                        decision,
                        Error(
                            AgentAuthorizationErrorCode.AuthorizationExpired,
                            "The proposed action time window is not current."),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (decision == AgentPolicyDecision.AuthorizedByYolo
                && !HasActiveYoloConfirmation(run, now))
            {
                return await DenyKnownAsync(
                        proposal,
                        tool,
                        permission,
                        decision,
                        Error(
                            AgentAuthorizationErrorCode.YoloConfirmationRequired,
                            "YOLO authority is not backed by a current explicit confirmation."),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return decision switch
            {
                AgentPolicyDecision.Denied => await DenyKnownAsync(
                        proposal,
                        tool,
                        permission,
                        decision,
                        Error(
                            AgentAuthorizationErrorCode.PolicyDenied,
                            "The authoritative run policy disables this capability."),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false),
                AgentPolicyDecision.RequiresApproval =>
                    _pendingApprovals.Count >= MaximumPendingApprovalCount
                        ? await DenyKnownAsync(
                                proposal,
                                tool,
                                permission,
                                decision,
                                CapacityExceeded("pending approvals"),
                                now,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : CreateApproval(proposal, tool, permission, now),
                AgentPolicyDecision.AuthorizedByAuto =>
                    _authorizations.Count >= MaximumAuthorizationCount
                        ? await DenyKnownAsync(
                                proposal,
                                tool,
                                permission,
                                decision,
                                CapacityExceeded("issued authorizations"),
                                now,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : await IssueAsync(
                                proposal,
                                tool,
                                permission,
                                decision,
                                AgentAuthorizationSource.AutoPolicy,
                                run.ApprovingClientId,
                                run.Agent,
                                now,
                                cancellationToken)
                            .ConfigureAwait(false),
                AgentPolicyDecision.AuthorizedByYolo =>
                    _authorizations.Count >= MaximumAuthorizationCount
                        ? await DenyKnownAsync(
                                proposal,
                                tool,
                                permission,
                                decision,
                                CapacityExceeded("issued authorizations"),
                                now,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : await IssueAsync(
                                proposal,
                                tool,
                                permission,
                                decision,
                                AgentAuthorizationSource.YoloPolicy,
                                run.ApprovingClientId,
                                run.YoloConfirmation!.ConfirmedBy,
                                now,
                                cancellationToken,
                                maximumExpiresAtUtc:
                                    run.YoloConfirmation.ExpiresAtUtc)
                            .ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"Unsupported policy decision '{decision}'."),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AgentAuthorizationResult> DecideAsync(
        AgentApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return Denied(Cancelled());
        }

        try
        {
            if (_disposed)
            {
                return Denied(BrokerDisposed());
            }

            if (!_pendingApprovals.TryGetValue(decision.ApprovalId, out var pending))
            {
                return Denied(Error(
                    AgentAuthorizationErrorCode.ApprovalNotFound,
                    "The approval request is no longer pending."));
            }

            if (pending.Revocation is not null)
            {
                return await FinishPendingDenialAsync(
                        decision.ApprovalId,
                        pending,
                        pending.Revocation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var now = _timeProvider.GetUtcNow();
            if (!_runs.TryGetValue(pending.Proposal.RunId, out var run))
            {
                return await RevokePendingAsync(
                        decision.ApprovalId,
                        pending,
                        new Revocation(RunNotFound(), decision.Actor, now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (decision.Actor.ClientId != run.ApprovingClientId)
            {
                return Denied(Error(
                    AgentAuthorizationErrorCode.ApprovalActorMismatch,
                    "The approval decision came from a different desktop client."));
            }

            var runError = ValidateProposalAgainstRun(pending.Proposal, run);
            if (runError is not null)
            {
                return await RevokePendingAsync(
                        decision.ApprovalId,
                        pending,
                        new Revocation(runError, decision.Actor, now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (pending.Request.ExpiresAtUtc <= now
                || pending.Proposal.DeadlineUtc <= now)
            {
                return await RevokePendingAsync(
                        decision.ApprovalId,
                        pending,
                        new Revocation(
                            Error(
                                AgentAuthorizationErrorCode.ApprovalExpired,
                                "The approval request has expired."),
                            decision.Actor,
                            now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (decision.DecidedAtUtc > now)
            {
                return Denied(Error(
                    AgentAuthorizationErrorCode.InvalidRequest,
                    "An approval decision cannot be dated in the future."));
            }

            if (!decision.Approved)
            {
                return await RevokePendingAsync(
                        decision.ApprovalId,
                        pending,
                        new Revocation(
                            Error(
                                AgentAuthorizationErrorCode.ApprovalDenied,
                                "The user denied this action."),
                            decision.Actor,
                            now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (decision.Duration != AgentApprovalDuration.Once)
            {
                return await RevokePendingAsync(
                        decision.ApprovalId,
                        pending,
                        new Revocation(
                            Error(
                                AgentAuthorizationErrorCode.InvalidRequest,
                                "This desktop version supports one-action approvals only."),
                            decision.Actor,
                            now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_authorizations.Count >= MaximumAuthorizationCount)
            {
                return await RevokePendingAsync(
                        decision.ApprovalId,
                        pending,
                        new Revocation(
                            CapacityExceeded("issued authorizations"),
                            decision.Actor,
                            now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = await IssueAsync(
                    pending.Proposal,
                    pending.Tool,
                    pending.Permission,
                    AgentPolicyDecision.RequiresApproval,
                    AgentAuthorizationSource.HumanApproval,
                    run.ApprovingClientId,
                    decision.Actor,
                    now,
                    cancellationToken,
                    pending.Request.Id,
                    AgentApprovalDuration.Once)
                .ConfigureAwait(false);
            if (result is AgentAuthorizationResult.Authorized)
            {
                _pendingApprovals.Remove(decision.ApprovalId);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AgentPermitResult> ConsumeAsync(
        AgentAuthorizationId authorizationId,
        AgentActionExecutionBinding currentBinding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentBinding);
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return PermitDenied(Cancelled());
        }

        try
        {
            if (_disposed)
            {
                return PermitDenied(BrokerDisposed());
            }

            if (!_authorizations.TryGetValue(authorizationId, out var issued))
            {
                return PermitDenied(Error(
                    AgentAuthorizationErrorCode.AuthorizationNotFound,
                    "The one-action authorization is unavailable or already consumed."));
            }

            if (issued.Revocation is not null)
            {
                return await FinishAuthorizationDenialAsync(
                        authorizationId,
                        issued,
                        issued.Revocation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var now = _timeProvider.GetUtcNow();
            if (issued.Authorization.ExpiresAtUtc <= now
                || issued.Proposal.DeadlineUtc <= now)
            {
                return await RevokeAuthorizationAsync(
                        authorizationId,
                        issued,
                        new Revocation(
                            Error(
                                AgentAuthorizationErrorCode.AuthorizationExpired,
                                "The one-action authorization has expired."),
                            issued.Proposal.Actor,
                            now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!Matches(issued, currentBinding))
            {
                return await RevokeAuthorizationAsync(
                        authorizationId,
                        issued,
                        new Revocation(
                            Error(
                                AgentAuthorizationErrorCode.AuthorizationMismatch,
                                "The action no longer matches its exact authorization."),
                            issued.Proposal.Actor,
                            now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!_runs.TryGetValue(issued.Proposal.RunId, out var run))
            {
                return await RevokeAuthorizationAsync(
                        authorizationId,
                        issued,
                        new Revocation(RunNotFound(), issued.Proposal.Actor, now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var runError = ValidateProposalAgainstRun(issued.Proposal, run);
            if (runError is not null)
            {
                return await RevokeAuthorizationAsync(
                        authorizationId,
                        issued,
                        new Revocation(runError, run.Agent, now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!PolicyStillAuthorizes(issued, run, now))
            {
                return await RevokeAuthorizationAsync(
                        authorizationId,
                        issued,
                        new Revocation(
                            Error(
                                AgentAuthorizationErrorCode.PolicyChanged,
                                "The authoritative run policy no longer authorizes this action."),
                            run.Agent,
                            now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_activeActions.Count + _pendingCompletionAudits.Count
                >= MaximumActiveActionCount)
            {
                return await RevokeAuthorizationAsync(
                        authorizationId,
                        issued,
                        new Revocation(
                            CapacityExceeded("active actions"),
                            run.Agent,
                            now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!_runAuthoritySignals.TryGetValue(
                    issued.Proposal.RunId,
                    out var runAuthoritySignal)
                || !runAuthoritySignal.TryCaptureGenerationToken(
                    issued.Authorization.PolicyGeneration,
                    out var runAuthorityToken))
            {
                return await RevokeAuthorizationAsync(
                        authorizationId,
                        issued,
                        new Revocation(
                            _cancelledRuns.ContainsKey(issued.Proposal.RunId)
                                ? RunCancelled()
                                : RunSuspended(),
                            run.Agent,
                            now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var startedAuditError = await AppendKnownAuditAsync(
                    issued.Proposal,
                    issued.Tool,
                    issued.Permission,
                    issued.Decision,
                    AuditOutcome.Started,
                    run.Agent,
                    issued.Authorization.Source,
                    errorCode: null,
                    resultCode: null,
                    now,
                    cancellationToken,
                    AuthorizationBinding(
                        issued.Proposal,
                        issued.Authorization))
                .ConfigureAwait(false);
            if (startedAuditError is not null)
            {
                return PermitDenied(startedAuditError);
            }

            if (runAuthorityToken.IsCancellationRequested)
            {
                var revocationError = _cancelledRuns.ContainsKey(issued.Proposal.RunId)
                    ? RunCancelled()
                    : Error(
                        AgentAuthorizationErrorCode.PolicyChanged,
                        "The run authority changed while the action was starting.");
                return await CancelStartedAuthorizationAsync(
                        authorizationId,
                        issued,
                        revocationError,
                        run.Agent,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                runAuthorityToken);
            var executionDeadlineUtc = Earliest(
                issued.Proposal.DeadlineUtc,
                now + issued.Tool.MaximumExecutionLifetime);
            if (issued.Authorization.Source
                == AgentAuthorizationSource.YoloPolicy)
            {
                // A YOLO confirmation is live authority, not merely an
                // issuance window. An in-flight action must stop when that
                // explicit confirmation window closes.
                executionDeadlineUtc = Earliest(
                    executionDeadlineUtc,
                    issued.Authorization.ExpiresAtUtc);
            }

            cancellationSource.CancelAfter(executionDeadlineUtc - now);
            var permit = new AgentActionPermit(
                issued.Authorization,
                now,
                cancellationSource.Token,
                executionDeadlineUtc);
            _authorizations.Remove(authorizationId);
            _activeActions.Add(
                authorizationId,
                new ActiveAction(issued, permit, cancellationSource));
            return new AgentPermitResult.Granted(permit);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal ValueTask<AgentPermitResult> ConsumeAsync(
        AgentAuthorizationId authorizationId,
        AgentActionProposal currentProposal,
        CancellationToken cancellationToken) =>
        ConsumeAsync(
            authorizationId,
            AgentActionExecutionBinding.FromProposal(currentProposal),
            cancellationToken);

    public async ValueTask<AgentAuthorizationError?> CompleteAsync(
        AgentActionPermit permit,
        AgentActionCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permit);
        ArgumentNullException.ThrowIfNull(completion);
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return Cancelled();
        }

        Task? completionAuditRevocation = null;
        AgentAuthorizationError? result = null;
        try
        {
            if (_disposed)
            {
                return BrokerDisposed();
            }

            var id = permit.Authorization.Id;
            if (_pendingCompletionAudits.TryGetValue(id, out var pending))
            {
                if (!ReferenceEquals(pending.Permit, permit)
                    || completion != pending.Completion)
                {
                    return Error(
                        AgentAuthorizationErrorCode.AlreadyCompleted,
                        "The action already has a different immutable completion pending audit.");
                }

                if (cancellationToken.IsCancellationRequested
                    || !await IsExactAuditEventDurableAsync(
                            pending.AuditEvent,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    var retryError = await AppendCompletionAuditAsync(
                            pending.AuditEvent,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (retryError is not null)
                    {
                        return retryError;
                    }
                }

                ResolvePendingCompletionAudit(id, pending);
                return null;
            }

            if (!_activeActions.TryGetValue(id, out var active)
                || !ReferenceEquals(active.Permit, permit))
            {
                return Error(
                    AgentAuthorizationErrorCode.AlreadyCompleted,
                    "The action is not active or its outcome was already recorded.");
            }

            if (completion.FinishedAtUtc < permit.StartedAtUtc)
            {
                return Error(
                    AgentAuthorizationErrorCode.InvalidRequest,
                    "The action completion precedes its start.");
            }

            var outcome = completion.Outcome switch
            {
                AgentActionOutcome.Succeeded => AuditOutcome.Succeeded,
                AgentActionOutcome.Failed => AuditOutcome.Failed,
                AgentActionOutcome.Cancelled => AuditOutcome.Cancelled,
                _ => throw new ArgumentOutOfRangeException(nameof(completion)),
            };
            var auditEvent = CreateAgentActionAuditEvent(
                active.Issued.Proposal,
                active.Issued.Proposal.Actor,
                outcome,
                AuditDetails.ForAgentAction(
                    active.Issued.Proposal.RunId,
                    active.Issued.Tool.Capability,
                    active.Issued.Tool.Risk,
                    active.Issued.Permission,
                    active.Issued.Decision,
                    active.Issued.Proposal.ArgumentDigest,
                    active.Issued.Authorization.Source,
                    errorCode: null,
                    completion.StableCode,
                    AuthorizationBinding(
                            active.Issued.Proposal,
                            active.Issued.Authorization)
                        .WithExecutionDuration(
                            completion.FinishedAtUtc - permit.StartedAtUtc)
                        .WithResultCount(completion.ResultCount)),
                completion.FinishedAtUtc);
            var auditError = await AppendCompletionAuditAsync(
                    auditEvent,
                    cancellationToken)
                .ConfigureAwait(false);
            _activeActions.Remove(id);
            active.CancellationSource.Dispose();
            if (auditError is not null)
            {
                _pendingCompletionAudits.Add(
                    id,
                    new PendingCompletionAudit(
                        active.Issued.Proposal.RunId,
                        permit,
                        completion,
                        auditEvent));
                if (_runs.TryGetValue(active.Issued.Proposal.RunId, out var run))
                {
                    run.Suspended = true;
                }

                if (_runAuthoritySignals.TryGetValue(
                        active.Issued.Proposal.RunId,
                        out var authoritySignal)
                    && authoritySignal.SuspendForCompletionAudit()
                        is { } revokedGeneration)
                {
                    completionAuditRevocation =
                        StartCancellationAndDispose(revokedGeneration);
                }
            }

            result = auditError;
        }
        finally
        {
            _gate.Release();
        }

        if (completionAuditRevocation is not null)
        {
            await completionAuditRevocation.ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        List<CancellationTokenSource> cancellations;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellations = [.. _activeActions.Values
                .Select(active => active.CancellationSource)
                .Distinct()];
            foreach (var signal in _runAuthoritySignals.Values)
            {
                signal.Dispose();
            }

            foreach (var run in _runs.Values)
            {
                run.Cancelled = true;
                run.Suspended = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        await CancelActiveActionsAsync(cancellations).ConfigureAwait(false);
        foreach (var cancellation in cancellations)
        {
            cancellation.Dispose();
        }

        _gate.Dispose();
    }

    private AgentAuthorizationResult CreateApproval(
        AgentActionProposal proposal,
        AgentToolDescriptor tool,
        AgentPermission permission,
        DateTimeOffset now)
    {
        var expiresAt = Earliest(
            proposal.DeadlineUtc,
            now + DefaultApprovalLifetime);
        var request = new AgentApprovalRequest(
            AgentApprovalId.New(),
            proposal,
            tool,
            permission,
            expiresAt);
        _pendingApprovals.Add(
            request.Id,
            new PendingApproval(request, proposal, tool, permission));
        return new AgentAuthorizationResult.ApprovalRequired(request);
    }

    private async ValueTask<AgentAuthorizationResult> IssueAsync(
        AgentActionProposal proposal,
        AgentToolDescriptor tool,
        AgentPermission permission,
        AgentPolicyDecision decision,
        AgentAuthorizationSource source,
        ClientId approvingClientId,
        ActorDescriptor decisionActor,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        AgentApprovalId? approvalId = null,
        AgentApprovalDuration? approvalDuration = null,
        DateTimeOffset? maximumExpiresAtUtc = null)
    {
        var expiresAt = Earliest(
            proposal.DeadlineUtc,
            now + DefaultAuthorizationLifetime);
        if (maximumExpiresAtUtc is { } maximumExpiry)
        {
            expiresAt = Earliest(expiresAt, maximumExpiry);
        }

        var authorization = new AgentActionAuthorization(
            AgentAuthorizationId.New(),
            proposal,
            tool,
            source,
            approvingClientId,
            expiresAt);
        var auditError = await AppendKnownAuditAsync(
                proposal,
                tool,
                permission,
                decision,
                AuditOutcome.Approved,
                decisionActor,
                source,
                errorCode: null,
                resultCode: null,
                now,
                cancellationToken,
                AuthorizationBinding(
                    proposal,
                    authorization,
                    approvalId,
                    approvalDuration))
            .ConfigureAwait(false);
        if (auditError is not null)
        {
            return Denied(auditError);
        }

        var issued = new IssuedAuthorization(
            authorization,
            proposal,
            tool,
            permission,
            decision);
        _authorizations.Add(
            authorization.Id,
            issued);
        var liveRunError = !_runs.TryGetValue(proposal.RunId, out var liveRun)
            ? _cancelledRuns.ContainsKey(proposal.RunId)
                ? RunCancelled()
                : RunNotFound()
            : ValidateProposalAgainstRun(proposal, liveRun);
        if (liveRunError is not null)
        {
            var denied = await RevokeAuthorizationAsync(
                    authorization.Id,
                    issued,
                    new Revocation(liveRunError, decisionActor, now),
                    cancellationToken)
                .ConfigureAwait(false);
            return denied is AgentPermitResult.Denied failure
                ? Denied(failure.Error)
                : throw new InvalidOperationException(
                    "Revoking an issued authorization must produce a denial.");
        }

        return new AgentAuthorizationResult.Authorized(authorization);
    }

    private async ValueTask<AgentAuthorizationResult> DenyKnownAsync(
        AgentActionProposal proposal,
        AgentToolDescriptor tool,
        AgentPermission permission,
        AgentPolicyDecision decision,
        AgentAuthorizationError error,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        ActorDescriptor? actor = null)
    {
        var auditError = await AppendKnownAuditAsync(
                proposal,
                tool,
                permission,
                decision,
                AuditOutcome.Denied,
                actor ?? proposal.Actor,
                authorizationSource: null,
                errorCode: error.Code,
                resultCode: null,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        return Denied(auditError ?? error);
    }

    private async ValueTask<AgentAuthorizationResult> DenyUntrustedAsync(
        AgentActionProposal proposal,
        AgentAuthorizationError error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requestedError = await AppendUnknownToolAuditAsync(
                proposal,
                AuditOutcome.Requested,
                proposal.Actor,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        if (requestedError is not null)
        {
            return Denied(requestedError);
        }

        if (_claimedActions.Count < MaximumClaimCount)
        {
            _claimedActions.TryAdd(proposal.Id, proposal.RunId);
        }

        var deniedError = await AppendUnknownToolAuditAsync(
                proposal,
                AuditOutcome.Denied,
                proposal.Actor,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        return Denied(deniedError ?? error);
    }

    private async ValueTask<AgentAuthorizationResult> DenyDuplicateAsync(
        AgentActionProposal proposal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var error = Error(
            AgentAuthorizationErrorCode.DuplicateAction,
            "The action ID has already been requested.");
        var auditError = await AppendDuplicateAttemptAuditAsync(
                proposal,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        return Denied(auditError ?? error);
    }

    private async ValueTask<AgentAuthorizationResult> RevokePendingAsync(
        AgentApprovalId approvalId,
        PendingApproval pending,
        Revocation revocation,
        CancellationToken cancellationToken)
    {
        pending.Revocation ??= revocation;
        return await FinishPendingDenialAsync(
                approvalId,
                pending,
                pending.Revocation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<AgentAuthorizationResult> FinishPendingDenialAsync(
        AgentApprovalId approvalId,
        PendingApproval pending,
        Revocation revocation,
        CancellationToken cancellationToken)
    {
        var auditError = await AppendKnownAuditAsync(
                pending.Proposal,
                pending.Tool,
                pending.Permission,
                AgentPolicyDecision.RequiresApproval,
                AuditOutcome.Denied,
                revocation.Actor,
                authorizationSource: null,
                errorCode: revocation.Error.Code,
                resultCode: null,
                revocation.OccurredAt,
                cancellationToken,
                ApprovalBinding(pending.Proposal, pending.Request))
            .ConfigureAwait(false);
        if (auditError is null)
        {
            _pendingApprovals.Remove(approvalId);
        }

        return Denied(auditError ?? revocation.Error);
    }

    private async ValueTask<AgentPermitResult> RevokeAuthorizationAsync(
        AgentAuthorizationId authorizationId,
        IssuedAuthorization issued,
        Revocation revocation,
        CancellationToken cancellationToken)
    {
        issued.Revocation ??= revocation;
        return await FinishAuthorizationDenialAsync(
                authorizationId,
                issued,
                issued.Revocation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<AgentPermitResult> FinishAuthorizationDenialAsync(
        AgentAuthorizationId authorizationId,
        IssuedAuthorization issued,
        Revocation revocation,
        CancellationToken cancellationToken)
    {
        var auditError = await AppendKnownAuditAsync(
                issued.Proposal,
                issued.Tool,
                issued.Permission,
                issued.Decision,
                AuditOutcome.Denied,
                revocation.Actor,
                issued.Authorization.Source,
                revocation.Error.Code,
                resultCode: null,
                revocation.OccurredAt,
                cancellationToken,
                AuthorizationBinding(
                    issued.Proposal,
                    issued.Authorization))
            .ConfigureAwait(false);
        if (auditError is null)
        {
            _authorizations.Remove(authorizationId);
        }

        return PermitDenied(auditError ?? revocation.Error);
    }

    private async ValueTask<AgentPermitResult> CancelStartedAuthorizationAsync(
        AgentAuthorizationId authorizationId,
        IssuedAuthorization issued,
        AgentAuthorizationError revocationError,
        ActorDescriptor actor,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var auditError = await AppendKnownAuditAsync(
                issued.Proposal,
                issued.Tool,
                issued.Permission,
                issued.Decision,
                AuditOutcome.Cancelled,
                actor,
                issued.Authorization.Source,
                errorCode: revocationError.Code,
                resultCode: "authority_revoked_before_dispatch",
                occurredAt,
                cancellationToken,
                AuthorizationBinding(
                        issued.Proposal,
                        issued.Authorization)
                    .WithExecutionDuration(TimeSpan.Zero))
            .ConfigureAwait(false);
        if (auditError is null)
        {
            _authorizations.Remove(authorizationId);
        }

        return PermitDenied(auditError ?? revocationError);
    }

    private async ValueTask<AgentAuthorizationError?> RevokeInactiveActionsAsync(
        AgentRunId runId,
        Revocation revocation,
        CancellationToken cancellationToken)
    {
        AgentAuthorizationError? firstAuditError = null;
        foreach (var entry in _pendingApprovals
            .Where(entry => entry.Value.Proposal.RunId == runId)
            .ToArray())
        {
            entry.Value.Revocation ??= revocation;
            var result = await FinishPendingDenialAsync(
                    entry.Key,
                    entry.Value,
                    entry.Value.Revocation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is AgentAuthorizationResult.Denied
                {
                    Error.Code: AgentAuthorizationErrorCode.AuditUnavailable
                        or AgentAuthorizationErrorCode.Cancelled,
                } denied)
            {
                firstAuditError ??= denied.Error;
            }
        }

        foreach (var entry in _authorizations
            .Where(entry => entry.Value.Proposal.RunId == runId)
            .ToArray())
        {
            entry.Value.Revocation ??= revocation;
            var result = await FinishAuthorizationDenialAsync(
                    entry.Key,
                    entry.Value,
                    entry.Value.Revocation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is AgentPermitResult.Denied
                {
                    Error.Code: AgentAuthorizationErrorCode.AuditUnavailable
                        or AgentAuthorizationErrorCode.Cancelled,
                } denied)
            {
                firstAuditError ??= denied.Error;
            }
        }

        return firstAuditError;
    }

    private async ValueTask<AgentAuthorizationError?> SweepExpiredInactiveActionsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AgentAuthorizationError? firstAuditError = null;
        foreach (var entry in _pendingApprovals
            .Where(entry =>
                entry.Value.Revocation is not null
                || entry.Value.Request.ExpiresAtUtc <= now
                || entry.Value.Proposal.DeadlineUtc <= now)
            .ToArray())
        {
            entry.Value.Revocation ??= new Revocation(
                Error(
                    AgentAuthorizationErrorCode.ApprovalExpired,
                    "The approval request has expired."),
                entry.Value.Proposal.Actor,
                now);
            var result = await FinishPendingDenialAsync(
                    entry.Key,
                    entry.Value,
                    entry.Value.Revocation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is AgentAuthorizationResult.Denied
                {
                    Error.Code: AgentAuthorizationErrorCode.AuditUnavailable
                        or AgentAuthorizationErrorCode.Cancelled,
                } denied)
            {
                firstAuditError ??= denied.Error;
            }
        }

        foreach (var entry in _authorizations
            .Where(entry =>
                entry.Value.Revocation is not null
                || entry.Value.Authorization.ExpiresAtUtc <= now
                || entry.Value.Proposal.DeadlineUtc <= now)
            .ToArray())
        {
            entry.Value.Revocation ??= new Revocation(
                Error(
                    AgentAuthorizationErrorCode.AuthorizationExpired,
                    "The one-action authorization has expired."),
                entry.Value.Proposal.Actor,
                now);
            var result = await FinishAuthorizationDenialAsync(
                    entry.Key,
                    entry.Value,
                    entry.Value.Revocation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is AgentPermitResult.Denied
                {
                    Error.Code: AgentAuthorizationErrorCode.AuditUnavailable
                        or AgentAuthorizationErrorCode.Cancelled,
                } denied)
            {
                firstAuditError ??= denied.Error;
            }
        }

        return firstAuditError;
    }

    private static AgentActionAuditBinding ProposalBinding(
        AgentActionProposal proposal) =>
        new(
            policyGeneration: proposal.PolicyGeneration,
            targetIdentity: proposal.TargetIdentity);

    private static AgentActionAuditBinding ApprovalBinding(
        AgentActionProposal proposal,
        AgentApprovalRequest approval) =>
        new(
            policyGeneration: proposal.PolicyGeneration,
            targetIdentity: proposal.TargetIdentity,
            approvalIdDigest: DigestApprovalId(approval.Id),
            approvalDuration: AgentApprovalDuration.Once,
            authorityExpiresAtUtc: approval.ExpiresAtUtc);

    private static AgentActionAuditBinding AuthorizationBinding(
        AgentActionProposal proposal,
        AgentActionAuthorization authorization,
        AgentApprovalId? approvalId = null,
        AgentApprovalDuration? approvalDuration = null) =>
        new(
            policyGeneration: proposal.PolicyGeneration,
            targetIdentity: proposal.TargetIdentity,
            approvalIdDigest: approvalId is { } concreteApprovalId
                ? DigestApprovalId(concreteApprovalId)
                : null,
            approvalDuration: approvalDuration,
            authorizationIdDigest: AgentActionDigest.FromUtf8(
                $"asura-agent-authorization-id-v1\0{authorization.Id.Value}"),
            authorityExpiresAtUtc: authorization.ExpiresAtUtc);

    private static AgentActionDigest DigestApprovalId(AgentApprovalId approvalId) =>
        AgentActionDigest.FromUtf8(
            $"asura-agent-approval-id-v1\0{approvalId.Value}");

    private async ValueTask<AgentAuthorizationError?> AppendKnownAuditAsync(
        AgentActionProposal proposal,
        AgentToolDescriptor tool,
        AgentPermission permission,
        AgentPolicyDecision decision,
        AuditOutcome outcome,
        ActorDescriptor actor,
        AgentAuthorizationSource? authorizationSource,
        AgentAuthorizationErrorCode? errorCode,
        string? resultCode,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken,
        AgentActionAuditBinding? binding = null) =>
        await AppendAuditAsync(
                proposal,
                actor,
                outcome,
                AuditDetails.ForAgentAction(
                    proposal.RunId,
                    tool.Capability,
                    tool.Risk,
                    permission,
                    decision,
                    proposal.ArgumentDigest,
                    authorizationSource,
                    errorCode,
                    resultCode,
                    binding ?? ProposalBinding(proposal)),
                occurredAt,
                cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<AgentAuthorizationError?> AppendUnknownToolAuditAsync(
        AgentActionProposal proposal,
        AuditOutcome outcome,
        ActorDescriptor actor,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        await AppendAuditAsync(
                proposal,
                actor,
                outcome,
                AuditDetails.None,
                occurredAt,
                cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<AgentAuthorizationError?> AppendAuditAsync(
        AgentActionProposal proposal,
        ActorDescriptor actor,
        AuditOutcome outcome,
        AuditDetails details,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var auditEvent = CreateAgentActionAuditEvent(
            proposal,
            actor,
            outcome,
            details,
            occurredAt);
        if (outcome == AuditOutcome.Requested)
        {
            var claim = await _auditStore
                .ClaimAgentActionAsync(auditEvent, cancellationToken)
                .ConfigureAwait(false);
            if (claim.IsSuccess)
            {
                return claim.Value == AgentActionAuditClaimOutcome.Claimed
                    ? null
                    : Error(
                        AgentAuthorizationErrorCode.DuplicateAction,
                        "The action ID has already been requested.");
            }

            return MapAuditError(claim.Error);
        }

        var result = await _auditStore
            .AppendAgentActionPhaseAsync(auditEvent, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? null : MapAuditError(result.Error);
    }

    private async ValueTask<AgentAuthorizationError?> AppendCompletionAuditAsync(
        AuditEventRecord auditEvent,
        CancellationToken cancellationToken)
    {
        var append = await _auditStore
            .AppendAgentActionPhaseAsync(auditEvent, cancellationToken)
            .ConfigureAwait(false);
        if (append.IsSuccess)
        {
            return null;
        }

        if (!cancellationToken.IsCancellationRequested
            && await IsExactAuditEventDurableAsync(
                    auditEvent,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        return MapAuditError(append.Error);
    }

    private async ValueTask<bool> IsExactAuditEventDurableAsync(
        AuditEventRecord auditEvent,
        CancellationToken cancellationToken)
    {
        var existing = await _auditStore
            .ListByCorrelationAsync(auditEvent.CorrelationId, cancellationToken)
            .ConfigureAwait(false);
        return existing.IsSuccess
            && existing.Value?.Any(candidate => candidate == auditEvent) == true;
    }

    private void ResolvePendingCompletionAudit(
        AgentAuthorizationId authorizationId,
        PendingCompletionAudit pending)
    {
        _pendingCompletionAudits.Remove(authorizationId);
        if (_runAuthoritySignals.TryGetValue(
                pending.RunId,
                out var authoritySignal))
        {
            authoritySignal.ResumeAfterCompletionAudit();
        }

        if (!_runs.TryGetValue(pending.RunId, out var run)
            || run.Cancelled
            || run.PendingPolicyAuditEvent is not null
            || _pendingCompletionAudits.Values.Any(
                candidate => candidate.RunId == pending.RunId))
        {
            return;
        }

        run.Suspended = false;
    }

    private static AuditEventRecord CreateAgentActionAuditEvent(
        AgentActionProposal proposal,
        ActorDescriptor actor,
        AuditOutcome outcome,
        AuditDetails details,
        DateTimeOffset occurredAt) =>
        new(
            AgentAuditEventId.ForPhase(proposal.Id, outcome),
            proposal.Id.Value,
            actor,
            proposal.ToolName,
            new AuditTarget(AuditTargetKind, proposal.TargetFingerprint.Value),
            outcome,
            details,
            occurredAt);

    private async ValueTask<AgentAuthorizationError?> AppendDuplicateAttemptAuditAsync(
        AgentActionProposal proposal,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var result = await _auditStore.AppendAsync(
                new AuditEventRecord(
                    Guid.CreateVersion7().ToString("N"),
                    $"duplicate-agent-action:{proposal.Id.Value}",
                    proposal.Actor,
                    proposal.ToolName,
                    new AuditTarget(AuditTargetKind, proposal.TargetFingerprint.Value),
                    AuditOutcome.Denied,
                    AuditDetails.None,
                    occurredAt),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? null : MapAuditError(result.Error);
    }

    private async ValueTask<AgentAuthorizationError?> AppendPolicyTransitionAuditAsync(
        AuditEventRecord auditEvent,
        CancellationToken cancellationToken)
    {
        var append = await _auditStore
            .AppendAsync(auditEvent, cancellationToken)
            .ConfigureAwait(false);
        if (append.IsSuccess)
        {
            return null;
        }

        if (append.Error?.Code != AuditStoreErrorCode.Conflict)
        {
            return MapAuditError(append.Error);
        }

        var existing = await _auditStore
            .ListByCorrelationAsync(auditEvent.CorrelationId, cancellationToken)
            .ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return MapAuditError(existing.Error);
        }

        return existing.Value?.Any(candidate => candidate == auditEvent) == true
            ? null
            : MapAuditError(append.Error);
    }

    private async ValueTask<AgentAuthorizationError?> RecordCapabilityRequestedAsync(
        RunAuthority run,
        AgentCapabilityRequestAuditEvent.Requested requested,
        CancellationToken cancellationToken)
    {
        if (run.Cancelled)
        {
            return RunCancelled();
        }

        var targetIdentity = AgentTargetIdentity.Create(requested.Target);
        if (requested.RunId != run.RunId
            || requested.PolicyGeneration != run.PolicyGeneration
            || targetIdentity != AgentTargetIdentity.Create(run.Target)
            || run.Policy.GetPermission(requested.Capability) != AgentPermission.Off)
        {
            return Error(
                AgentAuthorizationErrorCode.PolicyChanged,
                "The capability request does not match current run authority.");
        }

        if (run.PendingCapabilityRequest is { } existing)
        {
            if (existing.RequestId != requested.RequestId
                || existing.Capability != requested.Capability
                || existing.PolicyGeneration != requested.PolicyGeneration
                || existing.TargetIdentity != targetIdentity)
            {
                return Error(
                    AgentAuthorizationErrorCode.AlreadyCompleted,
                    "The run already has a different capability request.");
            }

            var retryError = await AppendPolicyTransitionAuditAsync(
                    existing.RequestedEvent,
                    cancellationToken)
                .ConfigureAwait(false);
            if (retryError is null)
            {
                existing.RequestedDurable = true;
                run.Suspended = false;
            }

            return retryError;
        }

        if (run.Suspended)
        {
            return RunSuspended();
        }

        var auditRecord = new AuditEventRecord(
            AgentAuditEventId.ForCapabilityRequestRequested(requested.RequestId),
            requested.RequestId.Value,
            run.Agent,
            CapabilityRequestAuditAction,
            new AuditTarget(AuditTargetKind, targetIdentity.Value),
            AuditOutcome.Requested,
            AuditDetails.ForAgentCapabilityRequest(
                run.RunId,
                requested.Capability,
                requested.PolicyGeneration,
                targetIdentity),
            _timeProvider.GetUtcNow().ToUniversalTime());
        var pending = new PendingCapabilityRequestAudit(
            requested.RequestId,
            requested.Capability,
            requested.PolicyGeneration,
            targetIdentity,
            auditRecord);
        run.PendingCapabilityRequest = pending;
        var error = await AppendPolicyTransitionAuditAsync(
                auditRecord,
                cancellationToken)
            .ConfigureAwait(false);
        if (error is null)
        {
            pending.RequestedDurable = true;
        }
        else
        {
            run.Suspended = true;
        }

        return error;
    }

    private async ValueTask<AgentAuthorizationError?> RecordCapabilityTerminalAsync(
        RunAuthority run,
        AgentCapabilityRequestAuditEvent.Terminal terminal,
        CancellationToken cancellationToken)
    {
        var pending = run.PendingCapabilityRequest;
        if (pending is null
            || pending.RequestId != terminal.RequestId
            || terminal.RunId != run.RunId)
        {
            return Error(
                AgentAuthorizationErrorCode.AlreadyCompleted,
                "The capability request is not pending.");
        }

        if (!IsCapabilityDecisionActorValid(run, terminal))
        {
            return Error(
                AgentAuthorizationErrorCode.ApprovalActorMismatch,
                "The capability decision actor does not own this run.");
        }

        if (!pending.RequestedDurable)
        {
            var requestedError = await AppendPolicyTransitionAuditAsync(
                    pending.RequestedEvent,
                    cancellationToken)
                .ConfigureAwait(false);
            if (requestedError is not null)
            {
                run.Suspended = true;
                return requestedError;
            }

            pending.RequestedDurable = true;
        }

        pending.TerminalEvent ??= new AuditEventRecord(
            AgentAuditEventId.ForCapabilityRequestTerminal(terminal.RequestId),
            terminal.RequestId.Value,
            terminal.Actor,
            CapabilityRequestAuditAction,
            new AuditTarget(AuditTargetKind, pending.TargetIdentity.Value),
            CapabilityRequestOutcome(terminal.Decision),
            AuditDetails.ForAgentCapabilityRequest(
                run.RunId,
                pending.Capability,
                pending.PolicyGeneration,
                pending.TargetIdentity,
                terminal.Decision),
            _timeProvider.GetUtcNow().ToUniversalTime());
        var recordedDecision = ((AuditDetails.AgentCapabilityRequestDetails)
            pending.TerminalEvent.Details).Decision;
        if (recordedDecision != terminal.Decision)
        {
            run.Suspended = true;
            return Error(
                AgentAuthorizationErrorCode.AlreadyCompleted,
                "The capability request already has a different terminal outcome.");
        }

        var error = await AppendPolicyTransitionAuditAsync(
                pending.TerminalEvent,
                cancellationToken)
            .ConfigureAwait(false);
        if (error is null)
        {
            run.PendingCapabilityRequest = null;
            run.Suspended = false;
        }
        else
        {
            run.Suspended = true;
        }

        return error;
    }

    private static AuditOutcome CapabilityRequestOutcome(
        AgentCapabilityRequestAuditDecision decision) =>
        decision switch
        {
            AgentCapabilityRequestAuditDecision.Allowed => AuditOutcome.Approved,
            AgentCapabilityRequestAuditDecision.Denied => AuditOutcome.Denied,
            AgentCapabilityRequestAuditDecision.Expired or
                AgentCapabilityRequestAuditDecision.Cancelled => AuditOutcome.Cancelled,
            AgentCapabilityRequestAuditDecision.TargetChanged or
                AgentCapabilityRequestAuditDecision.CapabilityUnavailable or
                AgentCapabilityRequestAuditDecision.PolicyChanged or
                AgentCapabilityRequestAuditDecision.AuditFailed => AuditOutcome.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };

    private static bool IsCapabilityDecisionActorValid(
        RunAuthority run,
        AgentCapabilityRequestAuditEvent.Terminal terminal)
    {
        if (terminal.Decision is
            AgentCapabilityRequestAuditDecision.Allowed or
            AgentCapabilityRequestAuditDecision.Denied)
        {
            return terminal.Actor.Kind == ActorKind.Human
                && terminal.Actor.ClientId == run.ApprovingClientId;
        }

        return terminal.Actor == run.Agent
            || terminal.Actor.Kind == ActorKind.Human
                && terminal.Actor.ClientId == run.ApprovingClientId;
    }

    private static AgentAuthorizationError? ValidateCapabilityPolicyUpdate(
        RunAuthority run,
        AgentRunPolicyUpdate update)
    {
        if (update.CapabilityRequestId is not { } requestId)
        {
            return null;
        }

        var pending = run.PendingCapabilityRequest;
        if (pending is null
            || pending.RequestId != requestId
            || !pending.RequestedDurable
            || pending.TerminalEvent is not null
            || update.PolicyGeneration != pending.PolicyGeneration + 1
            || run.Policy.GetPermission(pending.Capability) != AgentPermission.Off
            || update.Policy.GetPermission(pending.Capability) != AgentPermission.Ask
            || AgentPolicy.Capabilities.Any(capability =>
                capability != pending.Capability
                && run.Policy.GetPermission(capability)
                    != update.Policy.GetPermission(capability))
            || !string.Equals(run.Policy.Provider, update.Policy.Provider, StringComparison.Ordinal)
            || !string.Equals(run.Policy.Model, update.Policy.Model, StringComparison.Ordinal)
            || run.Policy.CompactionModel != update.Policy.CompactionModel
            || run.Policy.TitleModel != update.Policy.TitleModel
            || !string.Equals(run.Policy.SystemPrompt, update.Policy.SystemPrompt, StringComparison.Ordinal)
            || run.YoloConfirmation != update.YoloConfirmation)
        {
            return Error(
                AgentAuthorizationErrorCode.PolicyChanged,
                "The policy transition does not match the audited Off-to-Ask request.");
        }

        return null;
    }

    private static AuditEventRecord CreatePolicyTransitionAuditEvent(
        RunAuthority run,
        AgentRunPolicyUpdate update,
        DateTimeOffset occurredAt)
    {
        var previousYolo = run.YoloConfirmation;
        var transition = (previousYolo, update.YoloConfirmation) switch
        {
            (null, not null) => AgentRunPolicyTransition.YoloEnabled,
            (not null, null) when occurredAt >= previousYolo.ExpiresAtUtc =>
                AgentRunPolicyTransition.YoloExpired,
            (not null, null) => AgentRunPolicyTransition.YoloDisabled,
            _ => AgentRunPolicyTransition.Updated,
        };
        var targetIdentity = AgentTargetIdentity.Create(run.Target);
        var yoloExpiresAtUtc =
            update.YoloConfirmation?.ExpiresAtUtc
            ?? previousYolo?.ExpiresAtUtc;
        return new AuditEventRecord(
            AgentAuditEventId.NewPolicyTransition(),
            run.RunId.Value,
            update.ChangedBy,
            PolicyAuditAction,
            new AuditTarget(AuditTargetKind, targetIdentity.Value),
            AuditOutcome.Succeeded,
            AuditDetails.ForAgentRunPolicyTransition(
                run.RunId,
                transition,
                update.PolicyGeneration,
                targetIdentity,
                yoloExpiresAtUtc,
                update.CapabilityRequestId),
            occurredAt.ToUniversalTime());
    }

    private static AgentAuthorizationError MapAuditError(AuditStoreError? error) =>
        error?.Code switch
        {
            AuditStoreErrorCode.Cancelled => Cancelled(),
            AuditStoreErrorCode.Conflict => Error(
                AgentAuthorizationErrorCode.AlreadyCompleted,
                "The durable agent action already has a conflicting outcome."),
            _ => Error(
                AgentAuthorizationErrorCode.AuditUnavailable,
                "The action was not authorized because its audit trail could not be persisted."),
        };

    private AgentAuthorizationError? ValidateProposalAgainstRun(
        AgentActionProposal proposal,
        RunAuthority run)
    {
        if (run.Cancelled || _cancelledRuns.ContainsKey(run.RunId))
        {
            return RunCancelled();
        }

        if (run.Suspended
            || !_runAuthoritySignals.TryGetValue(run.RunId, out var authoritySignal)
            || authoritySignal.IsPolicyUpdatePending)
        {
            return RunSuspended();
        }

        if (proposal.Actor != run.Agent)
        {
            return Error(
                AgentAuthorizationErrorCode.RunActorMismatch,
                "The proposal actor does not own the live agent run.");
        }

        if (proposal.PolicyGeneration != run.PolicyGeneration)
        {
            return Error(
                AgentAuthorizationErrorCode.PolicyChanged,
                "The proposal does not use the authoritative run policy generation.");
        }

        if (!AgentTargetScope.Contains(run.Target, proposal.Target))
        {
            return Error(
                AgentAuthorizationErrorCode.TargetOutsideRunScope,
                "The proposal target is outside the run's authorized scope.");
        }

        return null;
    }

    private static AgentAuthorizationError? ValidateYoloConfirmation(
        AgentPolicy policy,
        AgentYoloConfirmation? confirmation,
        AgentRunId runId,
        AgentTarget target,
        long policyGeneration,
        DateTimeOffset now,
        ClientId approvingClientId)
    {
        var usesYolo = AgentPolicy.Capabilities.Any(
            capability => policy.GetPermission(capability) == AgentPermission.Yolo);
        if (!usesYolo)
        {
            return confirmation is null
                ? null
                : Error(
                    AgentAuthorizationErrorCode.InvalidRequest,
                    "A YOLO confirmation cannot be attached to a policy without YOLO capabilities.");
        }

        if (confirmation is null)
        {
            return Error(
                AgentAuthorizationErrorCode.YoloConfirmationRequired,
                "YOLO policy requires an explicit scoped and expiring human confirmation.");
        }

        try
        {
            confirmation.ValidateFor(
                runId,
                target,
                policyGeneration,
                now,
                approvingClientId);
            return null;
        }
        catch (ArgumentException)
        {
            return Error(
                AgentAuthorizationErrorCode.YoloConfirmationRequired,
                "The YOLO confirmation does not match this run, target, generation, or time window.");
        }
    }

    private static bool HasActiveYoloConfirmation(
        RunAuthority run,
        DateTimeOffset now) =>
        run.YoloConfirmation is { } confirmation
        && confirmation.RunId == run.RunId
        && confirmation.PolicyGeneration == run.PolicyGeneration
        && confirmation.TargetIdentity == AgentTargetIdentity.Create(run.Target)
        && confirmation.ConfirmedAtUtc <= now
        && confirmation.ExpiresAtUtc > now;

    private static bool Matches(
        IssuedAuthorization issued,
        AgentActionExecutionBinding binding)
    {
        var authorization = issued.Authorization;
        return authorization.ActionId == binding.ActionId
            && authorization.RunId == binding.RunId
            && authorization.ActorId == binding.ActorId
            && string.Equals(authorization.ToolName, binding.ToolName, StringComparison.Ordinal)
            && authorization.TargetIdentity == binding.TargetIdentity
            && authorization.TargetFingerprint == binding.TargetFingerprint
            && authorization.ArgumentDigest == binding.ArgumentDigest
            && authorization.PolicyGeneration == binding.PolicyGeneration
            && issued.Proposal.Target == binding.Target;
    }

    private static bool PolicyStillAuthorizes(
        IssuedAuthorization issued,
        RunAuthority run,
        DateTimeOffset now)
    {
        if (issued.Authorization.PolicyGeneration != run.PolicyGeneration)
        {
            return false;
        }

        var permission = run.Policy.GetPermission(issued.Tool.Capability);
        var decision = AgentPolicyResolver.Evaluate(permission, issued.Tool.Risk);
        return issued.Authorization.Source switch
        {
            AgentAuthorizationSource.AutoPolicy =>
                decision == AgentPolicyDecision.AuthorizedByAuto,
            AgentAuthorizationSource.YoloPolicy =>
                decision == AgentPolicyDecision.AuthorizedByYolo
                && HasActiveYoloConfirmation(run, now),
            AgentAuthorizationSource.HumanApproval =>
                permission != AgentPermission.Off,
            _ => false,
        };
    }

    private IEnumerable<CancellationTokenSource> CollectActiveCancellations(
        AgentRunId runId) =>
        [.. _activeActions.Values
            .Where(active => active.Issued.Proposal.RunId == runId)
            .Select(active => active.CancellationSource)];

    private AgentAuthorizationError? BeginRunPolicyUpdate(
        AgentRunPolicyUpdate update,
        out RunAuthoritySignal? authoritySignal,
        out CancellationTokenSource? revokedGeneration,
        out PolicyUpdateLease? policyUpdateLease)
    {
        revokedGeneration = null;
        policyUpdateLease = null;
        if (!_runAuthoritySignals.TryGetValue(update.RunId, out authoritySignal))
        {
            return null;
        }

        return BeginRunPolicyUpdate(
            update,
            authoritySignal,
            out revokedGeneration,
            out policyUpdateLease);
    }

    private static AgentAuthorizationError? BeginRunPolicyUpdate(
        AgentRunPolicyUpdate update,
        RunAuthoritySignal authoritySignal,
        out CancellationTokenSource? revokedGeneration,
        out PolicyUpdateLease? policyUpdateLease)
    {
        revokedGeneration = null;
        policyUpdateLease = null;
        if (update.ChangedBy.ClientId != authoritySignal.ApprovingClientId)
        {
            return Error(
                AgentAuthorizationErrorCode.ApprovalActorMismatch,
                "The policy change came from a different desktop client.");
        }

        return authoritySignal.TryBeginPolicyUpdate(
                update.PolicyGeneration,
                out revokedGeneration,
                out policyUpdateLease)
            switch
        {
            PolicyUpdateSignalResult.Begun
                or PolicyUpdateSignalResult.Retry => null,
            PolicyUpdateSignalResult.Cancelled => RunCancelled(),
            PolicyUpdateSignalResult.Suspended => RunSuspended(),
            PolicyUpdateSignalResult.Stale => Error(
                AgentAuthorizationErrorCode.PolicyChanged,
                "A run policy update must advance the authoritative generation."),
            _ => throw new InvalidOperationException(
                "The policy-update signal returned an unsupported result."),
        };
    }

    private void SignalRunCancellation(AgentRunCancellation cancellation)
    {
        if (!_runAuthoritySignals.TryGetValue(
                cancellation.RunId,
                out var signal)
            || !signal.TryCancel(cancellation.Actor, out var generationSource))
        {
            return;
        }

        _cancelledRuns.TryAdd(cancellation.RunId, 0);
        if (generationSource is not null)
        {
            BeginCancellationAndDispose(generationSource);
        }
    }

    private static bool CanCancelRun(
        ActorDescriptor actor,
        ActorId agentId,
        ClientId approvingClientId) =>
        actor.Kind switch
        {
            ActorKind.Agent => actor.Id == agentId,
            ActorKind.Human => actor.ClientId == approvingClientId,
            _ => false,
        };

    private static void SignalActiveActions(
        IEnumerable<CancellationTokenSource> cancellations)
    {
        foreach (var cancellation in cancellations.Distinct())
        {
            BeginCancellation(cancellation);
        }
    }

    private static void BeginCancellation(CancellationTokenSource cancellation)
    {
        _ = StartCancellation(cancellation);
    }

    private static void BeginCancellationAndDispose(CancellationTokenSource cancellation)
    {
        _ = StartCancellationAndDispose(cancellation);
    }

    private static async Task StartCancellationAndDispose(
        CancellationTokenSource cancellation)
    {
        try
        {
            await StartCancellation(cancellation).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static Task StartCancellation(CancellationTokenSource cancellation)
    {
        try
        {
            return ObserveCancellationAsync(cancellation.CancelAsync());
        }
        catch (ObjectDisposedException)
        {
            // A durable action outcome may dispose the source while a stop
            // request races with completion.
            return Task.CompletedTask;
        }
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (AggregateException)
        {
            // Callback failures cannot restore the revoked authority.
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race.
        }
    }

    private static async ValueTask CancelActiveActionsAsync(
        IEnumerable<CancellationTokenSource> cancellations)
    {
        foreach (var cancellation in cancellations.Distinct())
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (AggregateException)
            {
                // The token is still cancelled. A consumer callback cannot
                // restore authority or prevent the broker from revoking a run.
            }
            catch (ObjectDisposedException)
            {
                // Completion may win the cancellation race and dispose the
                // source after its durable action outcome is recorded.
            }
        }
    }

    private async ValueTask<bool> EnterGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static DateTimeOffset Earliest(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static AgentAuthorizationResult Denied(AgentAuthorizationError error) =>
        new AgentAuthorizationResult.Denied(error);

    private static AgentPermitResult PermitDenied(AgentAuthorizationError error) =>
        new AgentPermitResult.Denied(error);

    private static AgentMcpRunAuthorityResult McpRunDenied(
        AgentAuthorizationError error) =>
        new AgentMcpRunAuthorityResult.Denied(error);

    private static AgentAuthorizationError Error(
        AgentAuthorizationErrorCode code,
        string message) =>
        new(code, message);

    private static AgentAuthorizationError Cancelled() =>
        Error(
            AgentAuthorizationErrorCode.Cancelled,
            "The authorization operation was cancelled.");

    private static AgentAuthorizationError BrokerDisposed() =>
        Error(
            AgentAuthorizationErrorCode.Cancelled,
            "The authorization broker has stopped.");

    private static AgentAuthorizationError RunNotFound() =>
        Error(
            AgentAuthorizationErrorCode.RunNotFound,
            "The agent run does not have live authorization state.");

    private static AgentAuthorizationError RunCancelled() =>
        Error(
            AgentAuthorizationErrorCode.RunCancelled,
            "The agent run has been cancelled.");

    private static AgentAuthorizationError RunSuspended() =>
        Error(
            AgentAuthorizationErrorCode.RunSuspended,
            "The agent run is suspended while authority is being revoked.");

    private static AgentAuthorizationError CapacityExceeded(string resource) =>
        Error(
            AgentAuthorizationErrorCode.CapacityExceeded,
            $"The bounded capacity for {resource} has been reached.");

    private sealed class RunAuthority(
        AgentRunId runId,
        ActorDescriptor agent,
        ClientId approvingClientId,
        AgentTarget target,
        AgentPolicy policy,
        long policyGeneration,
        AgentYoloConfirmation? yoloConfirmation)
    {
        public AgentRunId RunId { get; } = runId;

        public ActorDescriptor Agent { get; } = agent;

        public ClientId ApprovingClientId { get; } = approvingClientId;

        public AgentTarget Target { get; } = target;

        public AgentPolicy Policy { get; set; } = policy;

        public long PolicyGeneration { get; set; } = policyGeneration;

        public AgentYoloConfirmation? YoloConfirmation { get; set; } = yoloConfirmation;

        public AuditEventRecord? PendingPolicyAuditEvent { get; set; }

        public PendingCapabilityRequestAudit? PendingCapabilityRequest { get; set; }

        public bool Suspended { get; set; }

        public bool Cancelled { get; set; }
    }

    private sealed class PendingCapabilityRequestAudit(
        AgentCapabilityRequestId requestId,
        AgentCapability capability,
        long policyGeneration,
        AgentActionDigest targetIdentity,
        AuditEventRecord requestedEvent)
    {
        public AgentCapabilityRequestId RequestId { get; } = requestId;

        public AgentCapability Capability { get; } = capability;

        public long PolicyGeneration { get; } = policyGeneration;

        public AgentActionDigest TargetIdentity { get; } = targetIdentity;

        public AuditEventRecord RequestedEvent { get; } = requestedEvent;

        public bool RequestedDurable { get; set; }

        public AuditEventRecord? TerminalEvent { get; set; }
    }

    private sealed class PendingApproval(
        AgentApprovalRequest request,
        AgentActionProposal proposal,
        AgentToolDescriptor tool,
        AgentPermission permission)
    {
        public AgentApprovalRequest Request { get; } = request;

        public AgentActionProposal Proposal { get; } = proposal;

        public AgentToolDescriptor Tool { get; } = tool;

        public AgentPermission Permission { get; } = permission;

        public Revocation? Revocation { get; set; }
    }

    private sealed class IssuedAuthorization(
        AgentActionAuthorization authorization,
        AgentActionProposal proposal,
        AgentToolDescriptor tool,
        AgentPermission permission,
        AgentPolicyDecision decision)
    {
        public AgentActionAuthorization Authorization { get; } = authorization;

        public AgentActionProposal Proposal { get; } = proposal;

        public AgentToolDescriptor Tool { get; } = tool;

        public AgentPermission Permission { get; } = permission;

        public AgentPolicyDecision Decision { get; } = decision;

        public Revocation? Revocation { get; set; }
    }

    private sealed record ActiveAction(
        IssuedAuthorization Issued,
        AgentActionPermit Permit,
        CancellationTokenSource CancellationSource);

    private sealed record PendingCompletionAudit(
        AgentRunId RunId,
        AgentActionPermit Permit,
        AgentActionCompletion Completion,
        AuditEventRecord AuditEvent);

    private sealed class PolicyUpdateLease(long generation)
    {
        public long Generation { get; } = generation;
    }

    private enum PolicyUpdateSignalResult
    {
        Begun,
        Retry,
        Cancelled,
        Suspended,
        Stale,
    }

    /// <summary>
    /// Provides the lock-free-of-broker-I/O revocation edge for one run.
    /// The small private lock protects only in-memory token rotation; no
    /// callbacks or persistence run while it is held.
    /// </summary>
    private sealed class RunAuthoritySignal(
        ActorId agentId,
        ClientId approvingClientId,
        long policyGeneration) : IDisposable
    {
        private readonly Lock _sync = new();
        private CancellationTokenSource _generationSource = new();
        private long _policyGeneration = policyGeneration;
        private long? _pendingPolicyGeneration;
        private int _completionAuditSuspensionCount;
        private readonly HashSet<PolicyUpdateLease> _policyUpdateLeases = [];
        private bool _cancelled;

        public ActorId AgentId { get; } = agentId;

        public ClientId ApprovingClientId { get; } = approvingClientId;

        public bool IsPolicyUpdatePending
        {
            get
            {
                lock (_sync)
                {
                    return _pendingPolicyGeneration is not null;
                }
            }
        }

        public PolicyUpdateSignalResult TryBeginPolicyUpdate(
            long nextGeneration,
            out CancellationTokenSource? revokedGeneration,
            out PolicyUpdateLease? policyUpdateLease)
        {
            lock (_sync)
            {
                revokedGeneration = null;
                policyUpdateLease = null;
                if (_cancelled)
                {
                    return PolicyUpdateSignalResult.Cancelled;
                }

                if (_completionAuditSuspensionCount > 0)
                {
                    return PolicyUpdateSignalResult.Suspended;
                }

                if (_pendingPolicyGeneration is { } pendingGeneration)
                {
                    if (pendingGeneration != nextGeneration)
                    {
                        return PolicyUpdateSignalResult.Suspended;
                    }

                    policyUpdateLease = new PolicyUpdateLease(nextGeneration);
                    _policyUpdateLeases.Add(policyUpdateLease);
                    return PolicyUpdateSignalResult.Retry;
                }

                if (nextGeneration <= _policyGeneration)
                {
                    return PolicyUpdateSignalResult.Stale;
                }

                _pendingPolicyGeneration = nextGeneration;
                policyUpdateLease = new PolicyUpdateLease(nextGeneration);
                _policyUpdateLeases.Add(policyUpdateLease);
                revokedGeneration = _generationSource;
                _generationSource = new CancellationTokenSource();
                return PolicyUpdateSignalResult.Begun;
            }
        }

        public void CompletePolicyUpdate(PolicyUpdateLease policyUpdateLease)
        {
            lock (_sync)
            {
                if (_pendingPolicyGeneration != policyUpdateLease.Generation
                    || !_policyUpdateLeases.Contains(policyUpdateLease))
                {
                    throw new InvalidOperationException(
                        "The completed policy update does not own a pending lease.");
                }

                _policyGeneration = policyUpdateLease.Generation;
                _pendingPolicyGeneration = null;
                _policyUpdateLeases.Clear();
            }
        }

        public void AbortPolicyUpdate(PolicyUpdateLease policyUpdateLease)
        {
            lock (_sync)
            {
                if (_pendingPolicyGeneration != policyUpdateLease.Generation
                    || !_policyUpdateLeases.Remove(policyUpdateLease))
                {
                    return;
                }

                if (_policyUpdateLeases.Count == 0)
                {
                    _pendingPolicyGeneration = null;
                }
            }
        }

        public bool TryCaptureGenerationToken(
            long generation,
            out CancellationToken token)
        {
            lock (_sync)
            {
                if (_cancelled
                    || _completionAuditSuspensionCount > 0
                    || _pendingPolicyGeneration is not null
                    || generation != _policyGeneration)
                {
                    token = new CancellationToken(canceled: true);
                    return false;
                }

                token = _generationSource.Token;
                return true;
            }
        }

        public CancellationTokenSource? SuspendForCompletionAudit()
        {
            lock (_sync)
            {
                _completionAuditSuspensionCount++;
                if (_completionAuditSuspensionCount > 1 || _cancelled)
                {
                    return null;
                }

                var revokedGeneration = _generationSource;
                _generationSource = new CancellationTokenSource();
                return revokedGeneration;
            }
        }

        public void ResumeAfterCompletionAudit()
        {
            lock (_sync)
            {
                if (_completionAuditSuspensionCount <= 0)
                {
                    throw new InvalidOperationException(
                        "The run has no pending completion audit suspension.");
                }

                _completionAuditSuspensionCount--;
            }
        }

        public bool TryCancel(
            ActorDescriptor actor,
            out CancellationTokenSource? generationSource)
        {
            lock (_sync)
            {
                generationSource = null;
                if (!CanCancelRun(actor, AgentId, ApprovingClientId))
                {
                    return false;
                }

                if (_cancelled)
                {
                    return true;
                }

                _cancelled = true;
                generationSource = _generationSource;
                return true;
            }
        }

        public void Dispose()
        {
            CancellationTokenSource generationSource;
            lock (_sync)
            {
                if (_cancelled)
                {
                    return;
                }

                _cancelled = true;
                generationSource = _generationSource;
            }

            BeginCancellationAndDispose(generationSource);
        }
    }

    private sealed record Revocation(
        AgentAuthorizationError Error,
        ActorDescriptor Actor,
        DateTimeOffset OccurredAt);
}
