using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentCapabilityBrokerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AgentPermission.Ask)]
    [InlineData(AgentPermission.Auto)]
    public async Task McpRunAuthorityRequiresExactLiveAskOrAutoPolicy(
        AgentPermission permission)
    {
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.McpTools,
                permission),
        };
        await using var broker = await CreateRegisteredBrokerAsync(
            new RecordingAuditStore(),
            policy);

        var result = await broker.AcquireAsync(
            new AgentMcpRunAuthorityRequest(RunId(), Agent()),
            CancellationToken.None);

        var lease = Assert.IsType<
            AgentMcpRunAuthorityResult.Granted>(result).Lease;
        Assert.Equal(RunId(), lease.RunId);
        Assert.Equal(Agent(), lease.Agent);
        Assert.Equal(1, lease.PolicyGeneration);
        Assert.False(lease.RevocationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task McpRunAuthorityRejectsUnregisteredAndForgedActors()
    {
        await using var unregistered = CreateBroker(
            new RecordingAuditStore());
        var absent = await unregistered.AcquireAsync(
            new AgentMcpRunAuthorityRequest(RunId(), Agent()),
            CancellationToken.None);
        Assert.Equal(
            AgentAuthorizationErrorCode.RunNotFound,
            Assert.IsType<AgentMcpRunAuthorityResult.Denied>(
                absent).Error.Code);

        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.McpTools,
                AgentPermission.Ask),
        };
        await using var registered = await CreateRegisteredBrokerAsync(
            new RecordingAuditStore(),
            policy);
        var forged = await registered.AcquireAsync(
            new AgentMcpRunAuthorityRequest(
                RunId(),
                Agent() with { DisplayName = "Forged agent" }),
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.RunActorMismatch,
            Assert.IsType<AgentMcpRunAuthorityResult.Denied>(
                forged).Error.Code);
    }

    [Fact]
    public async Task McpRunAuthorityLeaseIsRevokedByPolicyChangeAndCancellation()
    {
        var askPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.McpTools,
                AgentPermission.Ask),
        };
        await using var broker = await CreateRegisteredBrokerAsync(
            new RecordingAuditStore(),
            askPolicy);
        var original = Assert.IsType<
            AgentMcpRunAuthorityResult.Granted>(
                await broker.AcquireAsync(
                    new AgentMcpRunAuthorityRequest(RunId(), Agent()),
                    CancellationToken.None));
        var autoPolicy = askPolicy with
        {
            Permissions = askPolicy.Permissions.SetItem(
                AgentCapability.McpTools,
                AgentPermission.Auto),
        };

        Assert.Null(await broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(
                RunId(),
                autoPolicy,
                policyGeneration: 2,
                Human()),
            CancellationToken.None));
        Assert.True(
            original.Lease.RevocationToken.IsCancellationRequested);

        var current = Assert.IsType<
            AgentMcpRunAuthorityResult.Granted>(
                await broker.AcquireAsync(
                    new AgentMcpRunAuthorityRequest(RunId(), Agent()),
                    CancellationToken.None));
        Assert.Equal(2, current.Lease.PolicyGeneration);

        Assert.Null(await broker.CancelRunAsync(
            new AgentRunCancellation(
                RunId(),
                Human(),
                "user_stop",
                Now),
            CancellationToken.None));
        Assert.True(current.Lease.RevocationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task McpRunAuthorityCannotBeGrantedDuringPolicyUpdate()
    {
        var audit = new RecordingAuditStore
        {
            BlockPredicate = item => string.Equals(item.Action, "agent.run.policy", StringComparison.Ordinal),
        };
        var askPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.McpTools,
                AgentPermission.Ask),
        };
        await using var broker = await CreateRegisteredBrokerAsync(
            audit,
            askPolicy);
        var update = broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(
                RunId(),
                AgentPolicy.Default,
                policyGeneration: 2,
                Human()),
            CancellationToken.None).AsTask();
        await audit.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var acquire = broker.AcquireAsync(
            new AgentMcpRunAuthorityRequest(RunId(), Agent()),
            CancellationToken.None).AsTask();

        Assert.False(acquire.IsCompleted);
        audit.ReleaseBlocked.TrySetResult();
        Assert.Null(await update);
        Assert.Equal(
            AgentAuthorizationErrorCode.PolicyDenied,
            Assert.IsType<AgentMcpRunAuthorityResult.Denied>(
                await acquire).Error.Code);
    }

    [Fact]
    public async Task McpRunAuthorityAcceptsCurrentFullAccessConfirmation()
    {
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions
                .SetItem(
                    AgentCapability.McpTools,
                    AgentPermission.Yolo),
        };
        var confirmation = new AgentYoloConfirmation(
            RunId(),
            WorkspaceTarget(),
            policyGeneration: 1,
            Human(),
            Now,
            Now.AddMinutes(15));
        await using var broker = await CreateRegisteredBrokerAsync(
            new RecordingAuditStore(),
            policy,
            yoloConfirmation: confirmation);

        var result = await broker.AcquireAsync(
            new AgentMcpRunAuthorityRequest(RunId(), Agent()),
            CancellationToken.None);

        var lease = Assert.IsType<AgentMcpRunAuthorityResult.Granted>(result).Lease;
        Assert.Equal(RunId(), lease.RunId);
        Assert.Equal(1, lease.PolicyGeneration);
        Assert.False(lease.RevocationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task RequestsRequireBrokerOwnedLiveRunAuthority()
    {
        var audit = new RecordingAuditStore();
        await using var broker = CreateBroker(audit);

        var result = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalReadScreen),
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.RunNotFound,
            Assert.IsType<AgentAuthorizationResult.Denied>(result).Error.Code);
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Denied],
            audit.Events.Select(item => item.Outcome));
    }

    [Fact]
    public async Task AutoAuthorizesObservationButMutationRequiresApproval()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);

        var read = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalReadScreen),
            CancellationToken.None);
        var write = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalSendText),
            CancellationToken.None);

        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(read);
        Assert.Equal(
            AgentAuthorizationSource.AutoPolicy,
            authorized.Authorization.Source);
        Assert.Equal(new ClientId("client-1"), authorized.Authorization.ApprovingClientId);
        Assert.Equal(Agent(), authorized.Authorization.Agent);
        Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(write);
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Approved, AuditOutcome.Requested],
            audit.Events.Select(item => item.Outcome));
    }

    [Fact]
    public async Task CompletionFlowsOnlyTheBoundedResultCountIntoAuditEvidence()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var proposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(proposal, CancellationToken.None));
        var granted = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                authorized.Authorization.Id,
                proposal,
                CancellationToken.None));

        var completed = await broker.CompleteAsync(
            granted.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Succeeded,
                "ok",
                Now.AddSeconds(1),
                resultCount: 17),
            CancellationToken.None);

        Assert.Null(completed);
        var terminalEvent = Assert.Single(
            audit.Events,
            item => string.Equals(item.CorrelationId, proposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded);
        var details = Assert.IsType<AuditDetails.AgentActionDetails>(
            terminalEvent.Details);
        Assert.Equal(17, details.Binding.ResultCount);
        Assert.All(
            audit.Events.Where(item => item != terminalEvent),
            item => Assert.Null(
                Assert.IsType<AuditDetails.AgentActionDetails>(
                    item.Details)
                .Binding
                .ResultCount));
    }

    [Fact]
    public async Task HumanApprovalProducesOneExactConsumableAuthorizationAndCompleteAudit()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var proposal = Proposal(
            BuiltInAgentTools.TerminalSendText,
            argumentMaterial: "text=rm -rf /secret-canary");
        var requested = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(proposal, CancellationToken.None));
        var approved = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.DecideAsync(
                new AgentApprovalDecision(
                    requested.Approval.Id,
                    Human(),
                    approved: true,
                    AgentApprovalDuration.Once,
                    Now),
                CancellationToken.None));

        var permit = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                approved.Authorization.Id,
                proposal,
                CancellationToken.None));
        var completed = await broker.CompleteAsync(
            permit.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Succeeded,
                "ok",
                Now.AddSeconds(1)),
            CancellationToken.None);
        var replay = await broker.ConsumeAsync(
            approved.Authorization.Id,
            proposal,
            CancellationToken.None);

        Assert.Null(completed);
        Assert.Equal(new ClientId("client-1"), approved.Authorization.ApprovingClientId);
        Assert.Equal(Agent(), permit.Permit.Authorization.Agent);
        Assert.Equal(
            [
                AuditOutcome.Requested,
                AuditOutcome.Approved,
                AuditOutcome.Started,
                AuditOutcome.Succeeded,
            ],
            audit.Events.Select(item => item.Outcome));
        Assert.IsType<AgentPermitResult.Denied>(replay);
        Assert.DoesNotContain(
            audit.Events,
            item => item.ToString()!.Contains("secret-canary", StringComparison.Ordinal));
        var terminal = Assert.IsType<AuditDetails.AgentActionDetails>(
            audit.Events[^1].Details);
        Assert.Equal("ok", terminal.ResultCode);
        Assert.Equal(AgentAuthorizationSource.HumanApproval, terminal.AuthorizationSource);
        Assert.Equal(1, terminal.Binding.PolicyGeneration);
        Assert.Equal(proposal.TargetIdentity, terminal.Binding.TargetIdentity);
        Assert.NotNull(terminal.Binding.AuthorizationIdDigest);
        Assert.Equal(1000, terminal.Binding.ExecutionDurationMilliseconds);
        var approvedDetails = Assert.IsType<AuditDetails.AgentActionDetails>(
            audit.Events[1].Details);
        Assert.NotNull(approvedDetails.Binding.ApprovalIdDigest);
        Assert.Equal(
            AgentApprovalDuration.Once,
            approvedDetails.Binding.ApprovalDuration);
    }

    [Fact]
    public async Task CompletionAuditFailureQuarantinesRunAndBlocksNewAuthority()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var activeProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var activeAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(activeProposal, CancellationToken.None));
        var peerProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var peerAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(peerProposal, CancellationToken.None));
        var heldProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var heldAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(heldProposal, CancellationToken.None));
        var active = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                activeAuthorization.Authorization.Id,
                activeProposal,
                CancellationToken.None));
        var peer = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                peerAuthorization.Authorization.Id,
                peerProposal,
                CancellationToken.None));
        audit.FailurePredicate = item => string.Equals(item.CorrelationId, activeProposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded;

        var completionError = await broker.CompleteAsync(
            active.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Succeeded,
                "ok",
                Now.AddSeconds(1)),
            CancellationToken.None);
        var nextRequest = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalReadScreen),
            CancellationToken.None);
        var heldConsume = await broker.ConsumeAsync(
            heldAuthorization.Authorization.Id,
            heldProposal,
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.AuditUnavailable,
            completionError!.Code);
        Assert.True(peer.Permit.CancellationToken.IsCancellationRequested);
        Assert.Equal(
            AgentAuthorizationErrorCode.RunSuspended,
            Assert.IsType<AgentAuthorizationResult.Denied>(nextRequest).Error.Code);
        Assert.Equal(
            AgentAuthorizationErrorCode.RunSuspended,
            Assert.IsType<AgentPermitResult.Denied>(heldConsume).Error.Code);
        Assert.DoesNotContain(
            audit.Events,
            item => string.Equals(item.CorrelationId, activeProposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactCompletionRetryPersistsOrReconcilesImmutableAudit(
        bool commitBeforeFailure)
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var proposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var authorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(proposal, CancellationToken.None));
        var active = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                authorization.Authorization.Id,
                proposal,
                CancellationToken.None));
        var completion = new AgentActionCompletion(
            AgentActionOutcome.Succeeded,
            "ok",
            Now.AddSeconds(1));
        if (commitBeforeFailure)
        {
            audit.CommitThenFailurePredicate = item => string.Equals(item.CorrelationId, proposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded;
            audit.ListFailureCount = 1;
        }
        else
        {
            audit.FailurePredicate = item => string.Equals(item.CorrelationId, proposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded;
        }

        var firstAttempt = await broker.CompleteAsync(
            active.Permit,
            completion,
            CancellationToken.None);
        audit.FailurePredicate = null;
        var retry = await broker.CompleteAsync(
            active.Permit,
            completion,
            CancellationToken.None);
        var resumedProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var resumed = await broker.RequestAsync(
            resumedProposal,
            CancellationToken.None);
        var resumedAuthorization =
            Assert.IsType<AgentAuthorizationResult.Authorized>(resumed);
        var resumedPermit = await broker.ConsumeAsync(
            resumedAuthorization.Authorization.Id,
            resumedProposal,
            CancellationToken.None);
        var replay = await broker.CompleteAsync(
            active.Permit,
            completion,
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.AuditUnavailable,
            firstAttempt!.Code);
        Assert.Null(retry);
        Assert.IsType<AgentPermitResult.Granted>(resumedPermit);
        Assert.Equal(
            AgentAuthorizationErrorCode.AlreadyCompleted,
            replay!.Code);
        Assert.Single(
            audit.Events,
            item => string.Equals(item.CorrelationId, proposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded);
    }

    [Fact]
    public async Task ChangedCompletionRetryIsRejectedAndOriginalPayloadRemainsPending()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var proposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var authorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(proposal, CancellationToken.None));
        var active = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                authorization.Authorization.Id,
                proposal,
                CancellationToken.None));
        var original = new AgentActionCompletion(
            AgentActionOutcome.Succeeded,
            "ok",
            Now.AddSeconds(1));
        audit.FailurePredicate = item => string.Equals(item.CorrelationId, proposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded;
        Assert.Equal(
            AgentAuthorizationErrorCode.AuditUnavailable,
            (await broker.CompleteAsync(
                active.Permit,
                original,
                CancellationToken.None))!.Code);
        audit.FailurePredicate = null;

        var changed = await broker.CompleteAsync(
            active.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Failed,
                "changed",
                Now.AddSeconds(2)),
            CancellationToken.None);
        var stillSuspended = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalReadScreen),
            CancellationToken.None);
        var exact = await broker.CompleteAsync(
            active.Permit,
            original,
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.AlreadyCompleted,
            changed!.Code);
        Assert.Equal(
            AgentAuthorizationErrorCode.RunSuspended,
            Assert.IsType<AgentAuthorizationResult.Denied>(stillSuspended).Error.Code);
        Assert.Null(exact);
        var terminal = Assert.Single(
            audit.Events,
            item => string.Equals(item.CorrelationId, proposal.Id.Value
, StringComparison.Ordinal) && item.Outcome is AuditOutcome.Succeeded
                    or AuditOutcome.Failed
                    or AuditOutcome.Cancelled);
        Assert.Equal(AuditOutcome.Succeeded, terminal.Outcome);
        Assert.Equal(Now.AddSeconds(1), terminal.OccurredAt);
        Assert.Equal(
            "ok",
            Assert.IsType<AuditDetails.AgentActionDetails>(terminal.Details).ResultCode);
    }

    [Fact]
    public async Task ChangedArgumentsConsumeTheTokenAndFailClosed()
    {
        await using var broker = await CreateRegisteredBrokerAsync(new RecordingAuditStore());
        var proposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(proposal, CancellationToken.None));
        var changed = Proposal(
            proposal.ToolName,
            id: proposal.Id,
            argumentMaterial: "different");

        var mismatch = await broker.ConsumeAsync(
            authorized.Authorization.Id,
            changed,
            CancellationToken.None);
        var replay = await broker.ConsumeAsync(
            authorized.Authorization.Id,
            proposal,
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.AuthorizationMismatch,
            Assert.IsType<AgentPermitResult.Denied>(mismatch).Error.Code);
        Assert.Equal(
            AgentAuthorizationErrorCode.AuthorizationNotFound,
            Assert.IsType<AgentPermitResult.Denied>(replay).Error.Code);
    }

    [Fact]
    public async Task ChangedTargetCannotReuseAnUnchangedRevisionFingerprint()
    {
        await using var broker = await CreateRegisteredBrokerAsync(new RecordingAuditStore());
        var proposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(proposal, CancellationToken.None));
        var changed = Proposal(
            proposal.ToolName,
            id: proposal.Id,
            target: new AgentTarget.Workspace(
                new WindowInstanceId("window-1"),
                new WorkspaceInstanceId("workspace-2")));

        var result = await broker.ConsumeAsync(
            authorized.Authorization.Id,
            changed,
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.AuthorizationMismatch,
            Assert.IsType<AgentPermitResult.Denied>(result).Error.Code);
    }

    [Fact]
    public async Task RunScopeAllowsNarrowingButNeverAnotherWorkspace()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var allowedPanel = new AgentTarget.Panel(
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-1"),
            new TabInstanceId("tab-1"),
            new PanelInstanceId("panel-1"));
        var otherWorkspace = new AgentTarget.Panel(
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-2"),
            new TabInstanceId("tab-2"),
            new PanelInstanceId("panel-2"));

        var allowed = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalReadScreen, target: allowedPanel),
            CancellationToken.None);
        var denied = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalReadScreen, target: otherWorkspace),
            CancellationToken.None);

        Assert.IsType<AgentAuthorizationResult.Authorized>(allowed);
        Assert.Equal(
            AgentAuthorizationErrorCode.TargetOutsideRunScope,
            Assert.IsType<AgentAuthorizationResult.Denied>(denied).Error.Code);
    }

    [Fact]
    public async Task PolicyUpdateRevokesIssuedAuthorityAndCancelsActivePermits()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var issuedProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var issued = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(issuedProposal, CancellationToken.None));
        var activeProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var activeAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(activeProposal, CancellationToken.None));
        var active = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                activeAuthorization.Authorization.Id,
                activeProposal,
                CancellationToken.None));

        var updateError = await broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(
                RunId(),
                AgentPolicy.Default,
                policyGeneration: 2,
                Human()),
            CancellationToken.None);
        var revoked = await broker.ConsumeAsync(
            issued.Authorization.Id,
            issuedProposal,
            CancellationToken.None);

        Assert.Null(updateError);
        Assert.True(active.Permit.CancellationToken.IsCancellationRequested);
        Assert.Equal(
            AgentAuthorizationErrorCode.AuthorizationNotFound,
            Assert.IsType<AgentPermitResult.Denied>(revoked).Error.Code);

        Assert.Null(await broker.CompleteAsync(
            active.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Cancelled,
                "policy_changed",
                Now.AddSeconds(1)),
            CancellationToken.None));
    }

    [Fact]
    public async Task PolicyUpdateSignalsActivePermitWithoutWaitingForBlockedAuditIo()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var activeProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var activeAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(activeProposal, CancellationToken.None));
        var active = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                activeAuthorization.Authorization.Id,
                activeProposal,
                CancellationToken.None));
        var blockedProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        audit.BlockPredicate = item =>
            item.Outcome == AuditOutcome.Requested
            && string.Equals(item.CorrelationId, blockedProposal.Id.Value, StringComparison.Ordinal);
        var blockedRequest = broker.RequestAsync(
            blockedProposal,
            CancellationToken.None).AsTask();
        await audit.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = active.Permit.CancellationToken.Register(
            () => cancellationObserved.TrySetResult());

        var update = broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(RunId(), AgentPolicy.Default, 2, Human()),
            CancellationToken.None).AsTask();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(blockedRequest.IsCompleted);
        audit.ReleaseBlocked.TrySetResult();
        _ = await blockedRequest;
        Assert.Null(await update);
        Assert.Null(await broker.CompleteAsync(
            active.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Cancelled,
                "policy_changed",
                Now.AddSeconds(1)),
            CancellationToken.None));
    }

    [Fact]
    public async Task CancellingConcurrentPolicyRetryDoesNotAbortInitiatingRevocation()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var oldProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var oldAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(oldProposal, CancellationToken.None));
        audit.BlockPredicate = item =>
            item.Outcome == AuditOutcome.Denied
            && string.Equals(item.CorrelationId, oldProposal.Id.Value, StringComparison.Ordinal);

        var update = new AgentRunPolicyUpdate(
            RunId(),
            AgentPolicy.Default,
            policyGeneration: 2,
            Human());
        var initiatingUpdate = broker.UpdateRunPolicyAsync(
            update,
            CancellationToken.None).AsTask();
        await audit.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var retryCancellation = new CancellationTokenSource();
        var retry = broker.UpdateRunPolicyAsync(
            update,
            retryCancellation.Token).AsTask();
        retryCancellation.Cancel();

        Assert.Equal(
            AgentAuthorizationErrorCode.Cancelled,
            (await retry)!.Code);
        audit.ReleaseBlocked.TrySetResult();
        Assert.Null(await initiatingUpdate);

        var revoked = await broker.ConsumeAsync(
            oldAuthorization.Authorization.Id,
            oldProposal,
            CancellationToken.None);
        Assert.Equal(
            AgentAuthorizationErrorCode.AuthorizationNotFound,
            Assert.IsType<AgentPermitResult.Denied>(revoked).Error.Code);

        var currentProposal = Proposal(
            BuiltInAgentTools.TerminalReadScreen,
            policyGeneration: 2);
        var currentAuthorization =
            Assert.IsType<AgentAuthorizationResult.Authorized>(
                await broker.RequestAsync(
                    currentProposal,
                    CancellationToken.None));
        var currentPermit = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                currentAuthorization.Authorization.Id,
                currentProposal,
                CancellationToken.None));
        Assert.Null(await broker.CompleteAsync(
            currentPermit.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Succeeded,
                "ok",
                Now.AddSeconds(1)),
            CancellationToken.None));
    }

    [Fact]
    public async Task PolicyUpdateDuringStartedAuditCannotReturnAUsablePermit()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var proposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var authorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(proposal, CancellationToken.None));
        audit.BlockPredicate = item =>
            item.Outcome == AuditOutcome.Started
            && string.Equals(item.CorrelationId, proposal.Id.Value, StringComparison.Ordinal);
        var consume = broker.ConsumeAsync(
            authorization.Authorization.Id,
            proposal,
            CancellationToken.None).AsTask();
        await audit.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var update = broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(RunId(), AgentPolicy.Default, 2, Human()),
            CancellationToken.None).AsTask();
        audit.ReleaseBlocked.TrySetResult();

        var denied = Assert.IsType<AgentPermitResult.Denied>(await consume);
        Assert.Equal(AgentAuthorizationErrorCode.PolicyChanged, denied.Error.Code);
        Assert.Null(await update);
        Assert.Equal(
            [
                AuditOutcome.Requested,
                AuditOutcome.Approved,
                AuditOutcome.Started,
                AuditOutcome.Cancelled,
            ],
            audit.Events
                .Where(item => string.Equals(item.CorrelationId, proposal.Id.Value, StringComparison.Ordinal))
                .Select(item => item.Outcome));
    }

    [Fact]
    public async Task AuditFailureDuringPolicyRevocationSuspendsRunUntilRetry()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var proposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        _ = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(proposal, CancellationToken.None));
        audit.FailurePredicate = item => item.Outcome == AuditOutcome.Denied;

        var failed = await broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(RunId(), AgentPolicy.Default, 2, Human()),
            CancellationToken.None);
        audit.FailurePredicate = null;
        var suspended = await broker.RequestAsync(
            Proposal(
                BuiltInAgentTools.TerminalReadScreen,
                policyGeneration: 2),
            CancellationToken.None);
        var retry = await broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(RunId(), AgentPolicy.Default, 2, Human()),
            CancellationToken.None);

        Assert.Equal(AgentAuthorizationErrorCode.AuditUnavailable, failed!.Code);
        Assert.Equal(
            AgentAuthorizationErrorCode.RunSuspended,
            Assert.IsType<AgentAuthorizationResult.Denied>(suspended).Error.Code);
        Assert.Null(retry);
    }

    [Fact]
    public async Task YoloRequiresExplicitMatchingExpiringHumanConfirmation()
    {
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.DestructiveTerminalActions,
                AgentPermission.Yolo),
        };
        await using var broker = CreateBroker(new RecordingAuditStore());
        var missingConfirmation = await broker.RegisterRunAsync(
            Registration(policy),
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.YoloConfirmationRequired,
            missingConfirmation!.Code);

        var confirmation = new AgentYoloConfirmation(
            RunId(),
            WorkspaceTarget(),
            policyGeneration: 1,
            Human(),
            Now,
            Now.AddMinutes(30));
        Assert.Null(await broker.RegisterRunAsync(
            Registration(policy, confirmation),
            CancellationToken.None));
        var result = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(
                Proposal(BuiltInAgentTools.TerminalInterrupt),
                CancellationToken.None));

        Assert.Equal(AgentAuthorizationSource.YoloPolicy, result.Authorization.Source);
    }

    [Fact]
    public async Task YoloPermitIsCancelledAtTheConfirmedWindowBoundary()
    {
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Yolo),
        };
        var confirmation = new AgentYoloConfirmation(
            RunId(),
            WorkspaceTarget(),
            policyGeneration: 1,
            Human(),
            Now,
            Now.AddMilliseconds(100));
        await using var broker = await CreateRegisteredBrokerAsync(
            new RecordingAuditStore(),
            policy,
            yoloConfirmation: confirmation);
        var proposal = Proposal(BuiltInAgentTools.TerminalSendText);
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(proposal, CancellationToken.None));

        Assert.Equal(confirmation.ExpiresAtUtc, authorized.Authorization.ExpiresAtUtc);
        var granted = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                authorized.Authorization.Id,
                proposal,
                CancellationToken.None));
        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = granted.Permit.CancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            cancelled);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(granted.Permit.CancellationToken.IsCancellationRequested);
        Assert.Null(await broker.CompleteAsync(
            granted.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Cancelled,
                "yolo_expired",
                Now.AddMilliseconds(100)),
            CancellationToken.None));
    }

    [Fact]
    public async Task LongWaitExecutionWindowDoesNotLengthenIssuanceOrOtherTools()
    {
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.TerminalRead,
                AgentPermission.Auto),
        };
        await using var broker = await CreateRegisteredBrokerAsync(
            new RecordingAuditStore(),
            policy);
        var waitProposal = Proposal(
            BuiltInAgentTools.TerminalWait,
            lifetime: TimeSpan.FromMinutes(66));
        var waitAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(waitProposal, CancellationToken.None));

        Assert.Equal(
            Now + AgentCapabilityBroker.DefaultAuthorizationLifetime,
            waitAuthorization.Authorization.ExpiresAtUtc);
        var waitPermit = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                waitAuthorization.Authorization.Id,
                waitProposal,
                CancellationToken.None)).Permit;
        Assert.Equal(Now.AddMinutes(61), waitPermit.ExecutionDeadlineUtc);
        Assert.Null(await broker.CompleteAsync(
            waitPermit,
            new AgentActionCompletion(
                AgentActionOutcome.Succeeded,
                "wait_elapsed",
                Now),
            CancellationToken.None));

        var readProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var readAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(readProposal, CancellationToken.None));
        var readPermit = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                readAuthorization.Authorization.Id,
                readProposal,
                CancellationToken.None)).Permit;

        Assert.Equal(Now.AddSeconds(30), readPermit.ExecutionDeadlineUtc);
        Assert.Null(await broker.CompleteAsync(
            readPermit,
            new AgentActionCompletion(
                AgentActionOutcome.Succeeded,
                "screen_read",
                Now),
            CancellationToken.None));
    }

    [Fact]
    public async Task YoloRejectsConfirmationForAnotherTarget()
    {
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Yolo),
        };
        var confirmation = new AgentYoloConfirmation(
            RunId(),
            new AgentTarget.Workspace(
                new WindowInstanceId("window-1"),
                new WorkspaceInstanceId("other-workspace")),
            policyGeneration: 1,
            Human(),
            Now,
            Now.AddMinutes(15));
        await using var broker = CreateBroker(new RecordingAuditStore());

        var error = await broker.RegisterRunAsync(
            Registration(policy, confirmation),
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.YoloConfirmationRequired,
            error!.Code);
    }

    [Fact]
    public async Task YoloPolicyTransitionsAreDurablyAuditedWithExactScopeAndWindow()
    {
        var audit = new RecordingAuditStore();
        var time = new AdjustableTimeProvider(Now);
        await using var broker = CreateBroker(audit, time);
        Assert.Null(await broker.RegisterRunAsync(
            Registration(AgentPolicy.Default),
            CancellationToken.None));
        var yoloPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions
                .SetItem(AgentCapability.RunCommands, AgentPermission.Yolo)
                .SetItem(
                    AgentCapability.DestructiveTerminalActions,
                    AgentPermission.Yolo),
        };
        var expiresAt = Now.AddMinutes(15);
        var confirmation = new AgentYoloConfirmation(
            RunId(),
            WorkspaceTarget(),
            policyGeneration: 2,
            Human(),
            Now,
            expiresAt);

        Assert.Null(await broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(
                RunId(),
                yoloPolicy,
                policyGeneration: 2,
                Human(),
                confirmation),
            CancellationToken.None));
        time.Now = Now.AddMinutes(1);
        Assert.Null(await broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(
                RunId(),
                AgentPolicy.Default,
                policyGeneration: 3,
                Human()),
            CancellationToken.None));

        var transitions = audit.Events
            .Where(item =>
                item.Details is AuditDetails.AgentRunPolicyTransitionDetails)
            .ToArray();
        Assert.Collection(
            transitions,
            enabled =>
            {
                Assert.Equal("agent.run.policy", enabled.Action);
                Assert.Equal(RunId().Value, enabled.CorrelationId);
                Assert.Equal("agent-target-fingerprint", enabled.Target!.Kind);
                var details =
                    Assert.IsType<AuditDetails.AgentRunPolicyTransitionDetails>(
                        enabled.Details);
                Assert.Equal(
                    AgentRunPolicyTransition.YoloEnabled,
                    details.Transition);
                Assert.Equal(2, details.PolicyGeneration);
                Assert.Equal(
                    AgentTargetIdentity.Create(WorkspaceTarget()),
                    details.TargetIdentityDigest);
                Assert.Equal(expiresAt, details.YoloExpiresAtUtc);
            },
            disabled =>
            {
                var details =
                    Assert.IsType<AuditDetails.AgentRunPolicyTransitionDetails>(
                        disabled.Details);
                Assert.Equal(
                    AgentRunPolicyTransition.YoloDisabled,
                    details.Transition);
                Assert.Equal(3, details.PolicyGeneration);
                Assert.Equal(expiresAt, details.YoloExpiresAtUtc);
            });
    }

    [Fact]
    public async Task ExpiredYoloAndAmbiguousAuditCommitRemainReconstructable()
    {
        var audit = new RecordingAuditStore();
        var time = new AdjustableTimeProvider(Now);
        await using var broker = CreateBroker(audit, time);
        Assert.Null(await broker.RegisterRunAsync(
            Registration(AgentPolicy.Default),
            CancellationToken.None));
        var yoloPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Yolo),
        };
        var expiresAt = Now.AddMinutes(15);
        Assert.Null(await broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(
                RunId(),
                yoloPolicy,
                policyGeneration: 2,
                Human(),
                new AgentYoloConfirmation(
                    RunId(),
                    WorkspaceTarget(),
                    policyGeneration: 2,
                    Human(),
                    Now,
                    expiresAt)),
            CancellationToken.None));
        time.Now = expiresAt;
        audit.CommitThenConflictPredicate = item =>
            item.Details
                is AuditDetails.AgentRunPolicyTransitionDetails
            {
                Transition: AgentRunPolicyTransition.YoloExpired,
            };

        Assert.Null(await broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(
                RunId(),
                AgentPolicy.Default,
                policyGeneration: 3,
                Human()),
            CancellationToken.None));

        var expired = Assert.Single(
            audit.Events,
            item =>
                item.Details
                    is AuditDetails.AgentRunPolicyTransitionDetails
                {
                    Transition: AgentRunPolicyTransition.YoloExpired,
                });
        var details =
            Assert.IsType<AuditDetails.AgentRunPolicyTransitionDetails>(
                expired.Details);
        Assert.Equal(expiresAt, details.YoloExpiresAtUtc);
        Assert.Equal(3, details.PolicyGeneration);
    }

    [Fact]
    public async Task RestoredConversationCanReuseItsLivePolicyGeneration()
    {
        var audit = new RecordingAuditStore
        {
            RejectDuplicateEventIds = true,
        };
        var time = new AdjustableTimeProvider(Now);
        var fullAccessPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions
                .SetItem(AgentCapability.RunCommands, AgentPermission.Yolo)
                .SetItem(
                    AgentCapability.DestructiveTerminalActions,
                    AgentPermission.Yolo),
        };

        await using (var firstBroker = CreateBroker(audit, time))
        {
            Assert.Null(await firstBroker.RegisterRunAsync(
                Registration(AgentPolicy.Default),
                CancellationToken.None));
            Assert.Null(await firstBroker.UpdateRunPolicyAsync(
                new AgentRunPolicyUpdate(
                    RunId(),
                    fullAccessPolicy,
                    policyGeneration: 2,
                    Human(),
                    new AgentYoloConfirmation(
                        RunId(),
                        WorkspaceTarget(),
                        policyGeneration: 2,
                        Human(),
                        time.Now,
                        AgentYoloConfirmation.RunLifetimeExpiry)),
                CancellationToken.None));
        }

        time.Now = Now.AddDays(1);
        await using (var restoredBroker = CreateBroker(audit, time))
        {
            Assert.Null(await restoredBroker.RegisterRunAsync(
                Registration(AgentPolicy.Default),
                CancellationToken.None));
            Assert.Null(await restoredBroker.UpdateRunPolicyAsync(
                new AgentRunPolicyUpdate(
                    RunId(),
                    fullAccessPolicy,
                    policyGeneration: 2,
                    Human(),
                    new AgentYoloConfirmation(
                        RunId(),
                        WorkspaceTarget(),
                        policyGeneration: 2,
                        Human(),
                        time.Now,
                        AgentYoloConfirmation.RunLifetimeExpiry)),
                CancellationToken.None));
        }

        var transitions = audit.Events
            .Where(item =>
                item.Details is AuditDetails.AgentRunPolicyTransitionDetails)
            .ToArray();
        Assert.Equal(2, transitions.Length);
        Assert.Equal(2, transitions.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task PolicyTransitionAuditFailureSuspendsRunUntilExactRetry()
    {
        var audit = new RecordingAuditStore
        {
            FailurePredicate = item =>
                item.Details is AuditDetails.AgentRunPolicyTransitionDetails,
        };
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var update = new AgentRunPolicyUpdate(
            RunId(),
            AgentPolicy.Default,
            policyGeneration: 2,
            Human());

        var failed = await broker.UpdateRunPolicyAsync(
            update,
            CancellationToken.None);
        var suspended = await broker.RequestAsync(
            Proposal(
                BuiltInAgentTools.TerminalReadScreen,
                policyGeneration: 2),
            CancellationToken.None);
        audit.FailurePredicate = null;
        var retried = await broker.UpdateRunPolicyAsync(
            update,
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.AuditUnavailable,
            failed!.Code);
        Assert.Equal(
            AgentAuthorizationErrorCode.RunSuspended,
            Assert.IsType<AgentAuthorizationResult.Denied>(suspended).Error.Code);
        Assert.Null(retried);
        Assert.Single(
            audit.Events,
            item =>
                item.Details is AuditDetails.AgentRunPolicyTransitionDetails);
    }

    [Fact(DisplayName = "lifecycle.request-audit-failure suspends authority until exact retry")]
    [Trait("SecurityCampaignCase", "lifecycle.request-audit-failure")]
    public async Task CapabilityRequestAuditAmbiguityFailsClosedAndTerminalIsExactOnce()
    {
        var disabledPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Off),
        };
        var audit = new RecordingAuditStore
        {
            FailurePredicate = item => string.Equals(
                item.Action,
                "agent.capability.request",
                StringComparison.Ordinal),
        };
        await using var broker = await CreateRegisteredBrokerAsync(
            audit,
            disabledPolicy);
        var requestId = new AgentCapabilityRequestId("capability-request-1");
        var requested = new AgentCapabilityRequestAuditEvent.Requested(
            requestId,
            RunId(),
            AgentCapability.RunCommands,
            WorkspaceTarget(),
            policyGeneration: 1);

        var failed = await broker.RecordCapabilityRequestAuditAsync(
            requested,
            CancellationToken.None);
        var blocked = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalReadScreen),
            CancellationToken.None);
        audit.FailurePredicate = null;
        var retried = await broker.RecordCapabilityRequestAuditAsync(
            requested,
            CancellationToken.None);
        var denied = await broker.RecordCapabilityRequestAuditAsync(
            new AgentCapabilityRequestAuditEvent.Terminal(
                requestId,
                RunId(),
                AgentCapabilityRequestAuditDecision.Denied,
                Human()),
            CancellationToken.None);
        var secondTerminal = await broker.RecordCapabilityRequestAuditAsync(
            new AgentCapabilityRequestAuditEvent.Terminal(
                requestId,
                RunId(),
                AgentCapabilityRequestAuditDecision.Allowed,
                Human()),
            CancellationToken.None);

        Assert.Equal(AgentAuthorizationErrorCode.AuditUnavailable, failed!.Code);
        Assert.Equal(
            AgentAuthorizationErrorCode.RunSuspended,
            Assert.IsType<AgentAuthorizationResult.Denied>(blocked).Error.Code);
        Assert.Null(retried);
        Assert.Null(denied);
        Assert.Equal(
            AgentAuthorizationErrorCode.AlreadyCompleted,
            secondTerminal!.Code);
        var capabilityEvents = audit.Events.Where(item => string.Equals(
            item.Action,
            "agent.capability.request",
            StringComparison.Ordinal));
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Denied],
            capabilityEvents.Select(item => item.Outcome));
        Assert.Equal(
            2,
            capabilityEvents.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task CapabilityPolicyAuditAmbiguityAcceptsOnlyTheExactCorrelatedRetry()
    {
        var disabledPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Off),
        };
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(
            audit,
            disabledPolicy);
        var requestId = new AgentCapabilityRequestId("capability-request-1");
        Assert.Null(await broker.RecordCapabilityRequestAuditAsync(
            new AgentCapabilityRequestAuditEvent.Requested(
                requestId,
                RunId(),
                AgentCapability.RunCommands,
                WorkspaceTarget(),
                policyGeneration: 1),
            CancellationToken.None));
        var askPolicy = disabledPolicy with
        {
            Permissions = disabledPolicy.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Ask),
        };
        var update = new AgentRunPolicyUpdate(
            RunId(),
            askPolicy,
            policyGeneration: 2,
            Human(),
            capabilityRequestId: requestId);
        audit.FailurePredicate = item =>
            item.Details is AuditDetails.AgentRunPolicyTransitionDetails;

        var failed = await broker.UpdateRunPolicyAsync(
            update,
            CancellationToken.None);
        audit.FailurePredicate = null;
        var changedRetry = await broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(
                RunId(),
                askPolicy,
                policyGeneration: 2,
                Human()),
            CancellationToken.None);
        var exactRetry = await broker.UpdateRunPolicyAsync(
            update,
            CancellationToken.None);
        var terminal = await broker.RecordCapabilityRequestAuditAsync(
            new AgentCapabilityRequestAuditEvent.Terminal(
                requestId,
                RunId(),
                AgentCapabilityRequestAuditDecision.Allowed,
                Human()),
            CancellationToken.None);

        Assert.Equal(AgentAuthorizationErrorCode.AuditUnavailable, failed!.Code);
        Assert.Equal(AgentAuthorizationErrorCode.RunSuspended, changedRetry!.Code);
        Assert.Null(exactRetry);
        Assert.Null(terminal);
        var policyEvent = Assert.Single(
            audit.Events,
            item => item.Details is AuditDetails.AgentRunPolicyTransitionDetails);
        Assert.Equal(
            requestId,
            Assert.IsType<AuditDetails.AgentRunPolicyTransitionDetails>(
                policyEvent.Details).CapabilityRequestId);
    }

    [Fact]
    public async Task OnlyTheBoundDesktopClientCanApproveOrChangePolicy()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var pending = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(
                Proposal(BuiltInAgentTools.TerminalSendText),
                CancellationToken.None));

        var wrongApproval = await broker.DecideAsync(
            new AgentApprovalDecision(
                pending.Approval.Id,
                Human("client-2"),
                approved: true,
                AgentApprovalDuration.Once,
                Now),
            CancellationToken.None);
        var wrongPolicy = await broker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(
                RunId(),
                AgentPolicy.Default,
                2,
                Human("client-2")),
            CancellationToken.None);
        var correctApproval = await broker.DecideAsync(
            new AgentApprovalDecision(
                pending.Approval.Id,
                Human(),
                approved: true,
                AgentApprovalDuration.Once,
                Now),
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.ApprovalActorMismatch,
            Assert.IsType<AgentAuthorizationResult.Denied>(wrongApproval).Error.Code);
        Assert.Equal(
            AgentAuthorizationErrorCode.ApprovalActorMismatch,
            wrongPolicy!.Code);
        Assert.IsType<AgentAuthorizationResult.Authorized>(correctApproval);
    }

    [Fact]
    public async Task RunCancellationRevokesPendingAuthorityAndSignalsActiveWork()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var activeProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var activeAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(activeProposal, CancellationToken.None));
        var active = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                activeAuthorization.Authorization.Id,
                activeProposal,
                CancellationToken.None));
        var pending = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(
                Proposal(BuiltInAgentTools.TerminalSendText),
                CancellationToken.None));

        var cancelError = await broker.CancelRunAsync(
            new AgentRunCancellation(
                RunId(),
                Human(),
                "user_stop",
                Now),
            CancellationToken.None);
        var pendingDecision = await broker.DecideAsync(
            new AgentApprovalDecision(
                pending.Approval.Id,
                Human(),
                approved: true,
                AgentApprovalDuration.Once,
                Now),
            CancellationToken.None);
        var next = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalReadScreen),
            CancellationToken.None);

        Assert.Null(cancelError);
        Assert.True(active.Permit.CancellationToken.IsCancellationRequested);
        Assert.Equal(
            AgentAuthorizationErrorCode.ApprovalNotFound,
            Assert.IsType<AgentAuthorizationResult.Denied>(pendingDecision).Error.Code);
        Assert.Equal(
            AgentAuthorizationErrorCode.RunCancelled,
            Assert.IsType<AgentAuthorizationResult.Denied>(next).Error.Code);
        Assert.Null(await broker.CompleteAsync(
            active.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Cancelled,
                "user_stop",
                Now.AddSeconds(1)),
            CancellationToken.None));
    }

    [Fact]
    public async Task UnboundActorCannotCancelOrSignalAnotherRunsPermit()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var proposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var authorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(proposal, CancellationToken.None));
        var active = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                authorization.Authorization.Id,
                proposal,
                CancellationToken.None));

        var wrong = await broker.CancelRunAsync(
            new AgentRunCancellation(
                RunId(),
                Human("client-2"),
                "user_stop",
                Now),
            CancellationToken.None);

        Assert.Equal(AgentAuthorizationErrorCode.RunActorMismatch, wrong!.Code);
        Assert.False(active.Permit.CancellationToken.IsCancellationRequested);

        Assert.Null(await broker.CancelRunAsync(
            new AgentRunCancellation(RunId(), Human(), "user_stop", Now),
            CancellationToken.None));
        Assert.True(active.Permit.CancellationToken.IsCancellationRequested);
        Assert.Null(await broker.CompleteAsync(
            active.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Cancelled,
                "user_stop",
                Now.AddSeconds(1)),
            CancellationToken.None));
    }

    [Fact]
    public async Task RunStopSignalsActivePermitWithoutWaitingForBlockedAuditIo()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var activeProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        var activeAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(activeProposal, CancellationToken.None));
        var active = Assert.IsType<AgentPermitResult.Granted>(
            await broker.ConsumeAsync(
                activeAuthorization.Authorization.Id,
                activeProposal,
                CancellationToken.None));
        var blockedProposal = Proposal(BuiltInAgentTools.TerminalReadScreen);
        audit.BlockPredicate = item =>
            item.Outcome == AuditOutcome.Requested
            && string.Equals(item.CorrelationId, blockedProposal.Id.Value, StringComparison.Ordinal);
        var blockedRequest = broker.RequestAsync(
            blockedProposal,
            CancellationToken.None).AsTask();
        await audit.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = active.Permit.CancellationToken.Register(
            () => cancellationObserved.TrySetResult());

        var stop = broker.CancelRunAsync(
            new AgentRunCancellation(RunId(), Human(), "user_stop", Now),
            CancellationToken.None).AsTask();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(blockedRequest.IsCompleted);
        audit.ReleaseBlocked.TrySetResult();
        _ = await blockedRequest;
        Assert.Null(await stop);
        Assert.Null(await broker.CompleteAsync(
            active.Permit,
            new AgentActionCompletion(
                AgentActionOutcome.Cancelled,
                "user_stop",
                Now.AddSeconds(1)),
            CancellationToken.None));
    }

    [Fact]
    public async Task FailedDenialAuditCannotTurnTheSameApprovalIntoAuthority()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var pending = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(
                Proposal(BuiltInAgentTools.TerminalSendText),
                CancellationToken.None));
        audit.FailurePredicate = item => item.Outcome == AuditOutcome.Denied;

        var failedDenial = await broker.DecideAsync(
            new AgentApprovalDecision(
                pending.Approval.Id,
                Human(),
                approved: false,
                AgentApprovalDuration.Once,
                Now),
            CancellationToken.None);
        audit.FailurePredicate = null;
        var attemptedApproval = await broker.DecideAsync(
            new AgentApprovalDecision(
                pending.Approval.Id,
                Human(),
                approved: true,
                AgentApprovalDuration.Once,
                Now),
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.AuditUnavailable,
            Assert.IsType<AgentAuthorizationResult.Denied>(failedDenial).Error.Code);
        Assert.Equal(
            AgentAuthorizationErrorCode.ApprovalDenied,
            Assert.IsType<AgentAuthorizationResult.Denied>(attemptedApproval).Error.Code);
    }

    [Fact]
    public async Task AuditFailureCannotProduceAuthority()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        audit.FailurePredicate = _ => true;

        var result = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalReadScreen),
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.AuditUnavailable,
            Assert.IsType<AgentAuthorizationResult.Denied>(result).Error.Code);
    }

    [Fact]
    public async Task UnknownModelSuppliedToolIsDeniedAndAudited()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);

        var result = await broker.RequestAsync(
            Proposal("terminal.execute_anything"),
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.UnknownTool,
            Assert.IsType<AgentAuthorizationResult.Denied>(result).Error.Code);
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Denied],
            audit.Events.Select(item => item.Outcome));
    }

    [Fact]
    public async Task RepeatedActionIdCannotMintMultipleAuthorizations()
    {
        var audit = new RecordingAuditStore();
        await using var broker = await CreateRegisteredBrokerAsync(audit);
        var proposal = Proposal(BuiltInAgentTools.TerminalReadScreen);

        var first = await broker.RequestAsync(proposal, CancellationToken.None);
        var repeated = await broker.RequestAsync(proposal, CancellationToken.None);

        Assert.IsType<AgentAuthorizationResult.Authorized>(first);
        Assert.Equal(
            AgentAuthorizationErrorCode.DuplicateAction,
            Assert.IsType<AgentAuthorizationResult.Denied>(repeated).Error.Code);
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Approved, AuditOutcome.Denied],
            audit.Events.Select(item => item.Outcome));
    }

    [Fact]
    public async Task DisposeIsIdempotentAndConcurrentCallsFailClosed()
    {
        var broker = await CreateRegisteredBrokerAsync(new RecordingAuditStore());

        await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => broker.DisposeAsync().AsTask()));
        await broker.DisposeAsync();

        var request = await broker.RequestAsync(
            Proposal(BuiltInAgentTools.TerminalReadScreen),
            CancellationToken.None);
        var registration = await broker.RegisterRunAsync(
            new AgentRunRegistration(
                new AgentRunId("run-2"),
                Agent(),
                new ClientId("client-1"),
                WorkspaceTarget(),
                AgentPolicy.Default,
                1),
            CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.Cancelled,
            Assert.IsType<AgentAuthorizationResult.Denied>(request).Error.Code);
        Assert.Equal(AgentAuthorizationErrorCode.Cancelled, registration!.Code);
    }

    private static AgentCapabilityBroker CreateBroker(
        RecordingAuditStore audit,
        AdjustableTimeProvider? timeProvider = null) =>
        new(
            BuiltInAgentTools.Catalog,
            audit,
            timeProvider ?? new AdjustableTimeProvider(Now));

    private static async Task<AgentCapabilityBroker> CreateRegisteredBrokerAsync(
        RecordingAuditStore audit) =>
        await CreateRegisteredBrokerAsync(
            audit,
            AgentPolicy.Default);

    private static async Task<AgentCapabilityBroker> CreateRegisteredBrokerAsync(
        RecordingAuditStore audit,
        AgentPolicy policy,
        long policyGeneration = 1,
        AgentYoloConfirmation? yoloConfirmation = null)
    {
        var broker = CreateBroker(audit);
        var error = await broker.RegisterRunAsync(
            Registration(
                policy,
                yoloConfirmation,
                policyGeneration),
            CancellationToken.None);
        Assert.Null(error);
        return broker;
    }

    private static AgentRunRegistration Registration(
        AgentPolicy policy,
        AgentYoloConfirmation? yoloConfirmation = null,
        long policyGeneration = 1) =>
        new(
            RunId(),
            Agent(),
            new ClientId("client-1"),
            WorkspaceTarget(),
            policy,
            policyGeneration,
            yoloConfirmation);

    private static AgentActionProposal Proposal(
        string toolName,
        AgentActionId? id = null,
        long policyGeneration = 1,
        string argumentMaterial = "default",
        AgentTarget? target = null,
        ActorDescriptor? actor = null,
        TimeSpan? lifetime = null) =>
        new(
            id ?? AgentActionId.New(),
            RunId(),
            actor ?? Agent(),
            toolName,
            target ?? WorkspaceTarget(),
            AgentActionDigest.FromUtf8("window-1/workspace-1/revision-7"),
            AgentActionDigest.FromUtf8(argumentMaterial),
            new AgentApprovalPresentation(
                "Production shell",
                "server.example",
                "/srv/app",
                [new AgentApprovalArgument("Text", argumentMaterial)]),
            policyGeneration,
            Now,
            Now + (lifetime ?? TimeSpan.FromMinutes(10)));

    private static AgentRunId RunId() => new("run-1");

    private static AgentTarget.Workspace WorkspaceTarget() =>
        new(
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-1"));

    private static ActorDescriptor Agent() =>
        new(new ActorId("agent-1"), ActorKind.Agent, "Asura agent");

    private static ActorDescriptor Human(string clientId = "client-1") =>
        new(
            new ActorId(clientId),
            ActorKind.Human,
            "Local user",
            new ClientId(clientId));

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingAuditStore : IAuditStore
    {
        public List<AuditEventRecord> Events { get; } = [];

        public Func<AuditEventRecord, bool>? FailurePredicate { get; set; }

        public Func<AuditEventRecord, bool>? CommitThenConflictPredicate { get; set; }

        public Func<AuditEventRecord, bool>? CommitThenFailurePredicate { get; set; }

        public Func<AuditEventRecord, bool>? BlockPredicate { get; set; }

        public bool RejectDuplicateEventIds { get; set; }

        public int ListFailureCount { get; set; }

        public TaskCompletionSource Blocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            if (RejectDuplicateEventIds
                && Events.Any(item =>
                    string.Equals(
                        item.EventId,
                        auditEvent.EventId,
                        StringComparison.Ordinal)))
            {
                return AuditStoreResult<Unit>.Failure(
                    new AuditStoreError(
                        AuditStoreErrorCode.Conflict,
                        "The event ID already exists."));
            }

            if (FailurePredicate?.Invoke(auditEvent) == true)
            {
                return AuditStoreResult<Unit>.Failure(
                    new AuditStoreError(
                        AuditStoreErrorCode.StorageUnavailable,
                        "Unavailable."));
            }

            if (CommitThenConflictPredicate?.Invoke(auditEvent) == true)
            {
                CommitThenConflictPredicate = null;
                Events.Add(auditEvent);
                return AuditStoreResult<Unit>.Failure(
                    new AuditStoreError(
                        AuditStoreErrorCode.Conflict,
                        "The event committed before the result was lost."));
            }

            if (CommitThenFailurePredicate?.Invoke(auditEvent) == true)
            {
                CommitThenFailurePredicate = null;
                Events.Add(auditEvent);
                return AuditStoreResult<Unit>.Failure(
                    new AuditStoreError(
                        AuditStoreErrorCode.StorageUnavailable,
                        "The event committed before the result was lost."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (BlockPredicate?.Invoke(auditEvent) == true)
            {
                Blocked.TrySetResult();
                await ReleaseBlocked.Task.WaitAsync(cancellationToken);
            }

            Events.Add(auditEvent);
            return AuditStoreResult<Unit>.Success(Unit.Value);
        }

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ListFailureCount > 0)
            {
                ListFailureCount--;
                return ValueTask.FromResult(
                    AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Failure(
                        new AuditStoreError(
                            AuditStoreErrorCode.StorageUnavailable,
                            "Unavailable.")));
            }

            return ValueTask.FromResult(
                AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(
                    [.. Events.Where(item => string.Equals(item.CorrelationId, correlationId, StringComparison.Ordinal))]));
        }
    }
}
