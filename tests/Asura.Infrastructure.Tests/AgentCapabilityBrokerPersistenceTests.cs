using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class AgentCapabilityBrokerPersistenceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreviouslyClaimedActionCannotMintAuthorityAfterBrokerRestart()
    {
        await using var temporary = TemporaryDatabase.Create();
        var proposal = Proposal();
        await using (var first = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            new SqliteAuditStore(temporary.Database),
            new FixedTimeProvider(Now)))
        {
            Assert.Null(await first.RegisterRunAsync(
                Registration(),
                CancellationToken.None));
            Assert.IsType<AgentAuthorizationResult.Authorized>(
                await first.RequestAsync(proposal, CancellationToken.None));
        }

        await using var second = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            new SqliteAuditStore(temporary.Database),
            new FixedTimeProvider(Now));
        Assert.Null(await second.RegisterRunAsync(
            Registration(),
            CancellationToken.None));

        var replay = await second.RequestAsync(proposal, CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.DuplicateAction,
            Assert.IsType<AgentAuthorizationResult.Denied>(replay).Error.Code);
        var trail = await new SqliteAuditStore(temporary.Database)
            .ListByCorrelationAsync(proposal.Id.Value, CancellationToken.None);
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Approved],
            trail.Value!.Select(item => item.Outcome));
    }

    [Fact]
    public async Task RestoredConversationCanEnableFullAccessAtTheSameLiveGeneration()
    {
        await using var temporary = TemporaryDatabase.Create();
        var auditStore = new SqliteAuditStore(temporary.Database);

        await using (var first = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            auditStore,
            new FixedTimeProvider(Now)))
        {
            Assert.Null(await first.RegisterRunAsync(
                Registration(),
                CancellationToken.None));
            Assert.Null(await first.UpdateRunPolicyAsync(
                FullAccessUpdate(Now),
                CancellationToken.None));
        }

        var restoredAt = Now.AddDays(1);
        await using (var restored = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            auditStore,
            new FixedTimeProvider(restoredAt)))
        {
            Assert.Null(await restored.RegisterRunAsync(
                Registration(),
                CancellationToken.None));
            _ = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
                await restored.RequestAsync(
                    PanelFocusProposal(
                        "pending-focus",
                        policyGeneration: 1,
                        restoredAt),
                    CancellationToken.None));
            Assert.Null(await restored.UpdateRunPolicyAsync(
                FullAccessUpdate(restoredAt),
                CancellationToken.None));
            _ = Assert.IsType<AgentAuthorizationResult.Authorized>(
                await restored.RequestAsync(
                    PanelFocusProposal(
                        "retried-focus",
                        policyGeneration: 2,
                        restoredAt),
                    CancellationToken.None));
        }

        var trail = await auditStore.ListByCorrelationAsync(
            Registration().RunId.Value,
            CancellationToken.None);

        Assert.True(trail.IsSuccess, trail.Error?.Message);
        var transitions = trail.Value!
            .Where(item =>
                item.Details is AuditDetails.AgentRunPolicyTransitionDetails)
            .ToArray();
        Assert.Equal(2, transitions.Length);
        Assert.Equal(2, transitions.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task CapabilityRequestChainAndPolicyCorrelationSurviveSqliteReopen()
    {
        await using var temporary = TemporaryDatabase.Create();
        var requestId = new AgentCapabilityRequestId("capability-request-1");
        var disabledPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Off),
        };
        var registration = new AgentRunRegistration(
            Registration().RunId,
            Agent(),
            new ClientId("client-1"),
            Target(),
            disabledPolicy,
            policyGeneration: 1);
        await using (var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            new SqliteAuditStore(temporary.Database),
            new FixedTimeProvider(Now)))
        {
            Assert.Null(await broker.RegisterRunAsync(
                registration,
                CancellationToken.None));
            Assert.Null(await broker.RecordCapabilityRequestAuditAsync(
                new AgentCapabilityRequestAuditEvent.Requested(
                    requestId,
                    registration.RunId,
                    AgentCapability.RunCommands,
                    registration.Target,
                    registration.PolicyGeneration),
                CancellationToken.None));
            var askPolicy = disabledPolicy with
            {
                Permissions = disabledPolicy.Permissions.SetItem(
                    AgentCapability.RunCommands,
                    AgentPermission.Ask),
            };
            Assert.Null(await broker.UpdateRunPolicyAsync(
                new AgentRunPolicyUpdate(
                    registration.RunId,
                    askPolicy,
                    policyGeneration: 2,
                    Human(),
                    capabilityRequestId: requestId),
                CancellationToken.None));
            Assert.Null(await broker.RecordCapabilityRequestAuditAsync(
                new AgentCapabilityRequestAuditEvent.Terminal(
                    requestId,
                    registration.RunId,
                    AgentCapabilityRequestAuditDecision.Allowed,
                    Human()),
                CancellationToken.None));
        }

        var reopened = new SqliteAuditStore(temporary.Database);
        var requestTrail = await reopened.ListByCorrelationAsync(
            requestId.Value,
            CancellationToken.None);
        var policyTrail = await reopened.ListByCorrelationAsync(
            registration.RunId.Value,
            CancellationToken.None);

        Assert.True(requestTrail.IsSuccess, requestTrail.Error?.Message);
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Approved],
            requestTrail.Value!.Select(item => item.Outcome));
        Assert.All(
            requestTrail.Value!,
            item => Assert.IsType<AuditDetails.AgentCapabilityRequestDetails>(
                item.Details));
        Assert.Equal(
            requestId,
            Assert.IsType<AuditDetails.AgentRunPolicyTransitionDetails>(
                Assert.Single(policyTrail.Value!).Details).CapabilityRequestId);
    }

    private static AgentRunRegistration Registration() =>
        new(
            new AgentRunId("run-1"),
            Agent(),
            new ClientId("client-1"),
            Target(),
            AgentPolicy.Default,
            policyGeneration: 1);

    private static AgentActionProposal Proposal() =>
        new(
            new AgentActionId("action-1"),
            new AgentRunId("run-1"),
            Agent(),
            BuiltInAgentTools.TerminalReadScreen,
            Target(),
            AgentActionDigest.FromUtf8("workspace-revision-7"),
            AgentActionDigest.FromUtf8("read-screen"),
            new AgentApprovalPresentation(
                "Production shell",
                "server.example",
                "/srv/app"),
            policyGeneration: 1,
            Now,
            Now.AddMinutes(5));

    private static AgentActionProposal PanelFocusProposal(
        string actionId,
        long policyGeneration,
        DateTimeOffset requestedAt) =>
        new(
            new AgentActionId(actionId),
            Registration().RunId,
            Agent(),
            BuiltInAgentTools.PanelFocus,
            Target(),
            AgentActionDigest.FromUtf8("workspace-revision-7"),
            AgentActionDigest.FromUtf8(actionId),
            new AgentApprovalPresentation(
                "Local terminal",
                "localhost",
                null),
            policyGeneration,
            requestedAt,
            requestedAt.AddMinutes(5));

    private static AgentRunPolicyUpdate FullAccessUpdate(DateTimeOffset confirmedAt)
    {
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions
                .SetItem(AgentCapability.RunCommands, AgentPermission.Yolo)
                .SetItem(
                    AgentCapability.DestructiveTerminalActions,
                    AgentPermission.Yolo),
        };
        return new AgentRunPolicyUpdate(
            Registration().RunId,
            policy,
            policyGeneration: 2,
            Human(),
            new AgentYoloConfirmation(
                Registration().RunId,
                Target(),
                policyGeneration: 2,
                Human(),
                confirmedAt,
                AgentYoloConfirmation.RunLifetimeExpiry));
    }

    private static AgentTarget.Workspace Target() =>
        new(
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-1"));

    private static ActorDescriptor Agent() =>
        new(new ActorId("agent-1"), ActorKind.Agent, "Asura agent");

    private static ActorDescriptor Human() =>
        new(
            new ActorId("client-1"),
            ActorKind.Human,
            "Local user",
            new ClientId("client-1"));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
