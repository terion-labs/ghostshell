using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteAgentRunAuditReaderTests
{
    private static readonly DateTimeOffset ReferenceTime =
        new(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactRunPagesWholeValidatedActionsNewestFirst()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var reader = new SqliteAgentRunAuditReader(temporary.Database);
        var selectedRun = new AgentRunId("run-selected");
        await AppendSucceededActionAsync(
            store,
            selectedRun,
            "action-older",
            BuiltInAgentTools.TerminalReadScreen,
            ReferenceTime);
        await AppendSucceededActionAsync(
            store,
            selectedRun,
            "action-newer",
            BuiltInAgentTools.TerminalSendText,
            ReferenceTime.AddMinutes(1));
        await AppendSucceededActionAsync(
            store,
            new AgentRunId("run-other"),
            "action-other-run",
            BuiltInAgentTools.TerminalInterrupt,
            ReferenceTime.AddMinutes(2));

        var first = await reader.ReadAsync(
            new AgentRunAuditQuery(selectedRun, pageSize: 1),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.Message);
        var firstPage = first.Value!;
        var newest = Assert.IsType<AgentRunAuditActionEntry>(
            Assert.Single(firstPage.Entries));
        Assert.Equal(BuiltInAgentTools.TerminalSendText, newest.ToolName);
        Assert.Equal(AuditOutcome.Succeeded, newest.LatestOutcome);
        Assert.Equal(
            AgentActionDigest.FromUtf8("target:action-newer"),
            newest.TargetIdentity);
        Assert.Equal(
            [
                AuditOutcome.Requested,
                AuditOutcome.Approved,
                AuditOutcome.Started,
                AuditOutcome.Succeeded,
            ],
            newest.Phases.Select(phase => phase.Outcome));
        Assert.NotNull(firstPage.Next);

        var second = await reader.ReadAsync(
            new AgentRunAuditQuery(selectedRun, firstPage.Next, pageSize: 1),
            CancellationToken.None);

        Assert.True(second.IsSuccess, second.Error?.Message);
        var older = Assert.IsType<AgentRunAuditActionEntry>(
            Assert.Single(second.Value!.Entries));
        Assert.Equal(BuiltInAgentTools.TerminalReadScreen, older.ToolName);
        Assert.Null(second.Value.Next);
    }

    [Fact]
    public async Task CursorSnapshotDoesNotLoseAnUnseenActionThatLaterAdvances()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var reader = new SqliteAgentRunAuditReader(temporary.Database);
        var runId = new AgentRunId("run-stable-pagination");
        const string olderActionId = "action-older-pending";
        var olderRequestedAt = ReferenceTime;
        Assert.Equal(
            AgentActionAuditClaimOutcome.Claimed,
            Success(await store.ClaimAgentActionAsync(
                ActionEvent(
                    runId,
                    olderActionId,
                    BuiltInAgentTools.TerminalReadScreen,
                    AuditOutcome.Requested,
                    olderRequestedAt),
                CancellationToken.None)));
        await AppendSucceededActionAsync(
            store,
            runId,
            "action-newer-complete",
            BuiltInAgentTools.TerminalWait,
            ReferenceTime.AddMinutes(1));

        var firstPage = Success(await reader.ReadAsync(
            new AgentRunAuditQuery(runId, pageSize: 1),
            CancellationToken.None));

        Assert.Equal(
            BuiltInAgentTools.TerminalWait,
            Assert.IsType<AgentRunAuditActionEntry>(
                Assert.Single(firstPage.Entries)).ToolName);
        Assert.NotNull(firstPage.Next);

        await AppendSucceededPhasesAsync(
            store,
            runId,
            olderActionId,
            BuiltInAgentTools.TerminalReadScreen,
            olderRequestedAt);

        var secondPage = Success(await reader.ReadAsync(
            new AgentRunAuditQuery(runId, firstPage.Next, pageSize: 1),
            CancellationToken.None));

        var olderAtSnapshot = Assert.IsType<AgentRunAuditActionEntry>(
            Assert.Single(secondPage.Entries));
        Assert.Equal(BuiltInAgentTools.TerminalReadScreen, olderAtSnapshot.ToolName);
        Assert.Equal(
            [AuditOutcome.Requested],
            olderAtSnapshot.Phases.Select(phase => phase.Outcome));
        Assert.Null(secondPage.Next);
    }

    [Fact]
    public async Task CursorCannotBeSubstitutedAcrossRuns()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var reader = new SqliteAgentRunAuditReader(temporary.Database);
        var firstRun = new AgentRunId("run-first");
        await AppendSucceededActionAsync(
            store,
            firstRun,
            "action-first-a",
            BuiltInAgentTools.TerminalReadScreen,
            ReferenceTime);
        await AppendSucceededActionAsync(
            store,
            firstRun,
            "action-first-b",
            BuiltInAgentTools.TerminalWait,
            ReferenceTime.AddMinutes(1));
        var firstPage = Success(await reader.ReadAsync(
            new AgentRunAuditQuery(firstRun, pageSize: 1),
            CancellationToken.None));

        var substituted = await reader.ReadAsync(
            new AgentRunAuditQuery(
                new AgentRunId("run-second"),
                firstPage.Next,
                pageSize: 1),
            CancellationToken.None);

        Assert.False(substituted.IsSuccess);
        Assert.Equal(AuditStoreErrorCode.InvalidQuery, substituted.Error!.Code);
    }

    [Fact]
    public async Task SameCorrelationInAnotherRunDoesNotEnterSelectedTimeline()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var reader = new SqliteAgentRunAuditReader(temporary.Database);
        var selectedRun = new AgentRunId("run-selected-correlation");
        const string sharedCorrelation = "action-shared-correlation";
        await AppendSucceededActionAsync(
            store,
            selectedRun,
            sharedCorrelation,
            BuiltInAgentTools.TerminalReadScreen,
            ReferenceTime);
        var otherRunEvent = ActionEvent(
            new AgentRunId("run-other-correlation"),
            sharedCorrelation,
            BuiltInAgentTools.TerminalInterrupt,
            AuditOutcome.Requested,
            ReferenceTime.AddMinutes(1)) with
        {
            EventId = "other-run-shared-correlation",
        };
        Assert.True(
            (await store.AppendAsync(otherRunEvent, CancellationToken.None)).IsSuccess);

        var page = Success(await reader.ReadAsync(
            new AgentRunAuditQuery(selectedRun),
            CancellationToken.None));

        var action = Assert.IsType<AgentRunAuditActionEntry>(
            Assert.Single(page.Entries));
        Assert.Equal(BuiltInAgentTools.TerminalReadScreen, action.ToolName);
        Assert.Equal(AuditOutcome.Succeeded, action.LatestOutcome);
    }

    [Fact]
    public async Task InvalidPhaseSequenceFailsTheWholePageClosed()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var reader = new SqliteAgentRunAuditReader(temporary.Database);
        var runId = new AgentRunId("run-corrupt");
        var requested = ActionEvent(
            runId,
            "action-corrupt",
            BuiltInAgentTools.TerminalReadScreen,
            AuditOutcome.Requested,
            ReferenceTime);
        Assert.Equal(
            AgentActionAuditClaimOutcome.Claimed,
            Success(await store.ClaimAgentActionAsync(
                requested,
                CancellationToken.None)));
        Assert.True((await store.AppendAsync(
            ActionEvent(
                runId,
                requested.CorrelationId,
                requested.Action,
                AuditOutcome.Started,
                ReferenceTime.AddSeconds(1)),
            CancellationToken.None)).IsSuccess);

        var result = await reader.ReadAsync(
            new AgentRunAuditQuery(runId),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuditStoreErrorCode.StorageFailure, result.Error!.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task PolicyTransitionIsProjectedWithoutRawStorageShape()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var reader = new SqliteAgentRunAuditReader(temporary.Database);
        var runId = new AgentRunId("run-policy");
        var target = AgentActionDigest.FromUtf8("policy-target");
        var expiry = ReferenceTime.AddMinutes(15);
        var auditEvent = new AuditEventRecord(
            "event-policy-enabled",
            runId.Value,
            new ActorDescriptor(
                new ActorId("desktop-client"),
                ActorKind.Human,
                "Local user",
                new ClientId("desktop-client")),
            "agent.run.policy",
            new AuditTarget("agent-target-fingerprint", target.Value),
            AuditOutcome.Succeeded,
            AuditDetails.ForAgentRunPolicyTransition(
                runId,
                AgentRunPolicyTransition.YoloEnabled,
                2,
                target,
                expiry),
            ReferenceTime);
        Assert.True(
            (await store.AppendAsync(auditEvent, CancellationToken.None)).IsSuccess);

        var page = Success(await reader.ReadAsync(
            new AgentRunAuditQuery(runId),
            CancellationToken.None));

        var policy = Assert.IsType<AgentRunAuditPolicyEntry>(
            Assert.Single(page.Entries));
        Assert.Equal(AgentRunPolicyTransition.YoloEnabled, policy.Transition);
        Assert.Equal(2, policy.PolicyGeneration);
        Assert.Equal(target, policy.TargetIdentity);
        Assert.Equal(expiry, policy.YoloExpiresAtUtc);
    }

    [Fact]
    public async Task CancellationReturnsTypedReadFailure()
    {
        await using var temporary = TemporaryDatabase.Create();
        var reader = new SqliteAgentRunAuditReader(temporary.Database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await reader.ReadAsync(
            new AgentRunAuditQuery(new AgentRunId("run-cancelled")),
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuditStoreErrorCode.Cancelled, result.Error!.Code);
    }

    private static async Task AppendSucceededActionAsync(
        SqliteAuditStore store,
        AgentRunId runId,
        string actionId,
        string toolName,
        DateTimeOffset startedAt)
    {
        Assert.Equal(
            AgentActionAuditClaimOutcome.Claimed,
            Success(await store.ClaimAgentActionAsync(
                ActionEvent(
                    runId,
                    actionId,
                    toolName,
                    AuditOutcome.Requested,
                    startedAt),
                CancellationToken.None)));
        await AppendSucceededPhasesAsync(
            store,
            runId,
            actionId,
            toolName,
            startedAt);
    }

    private static async Task AppendSucceededPhasesAsync(
        SqliteAuditStore store,
        AgentRunId runId,
        string actionId,
        string toolName,
        DateTimeOffset startedAt)
    {
        Assert.True((await store.AppendAgentActionPhaseAsync(
            ActionEvent(
                runId,
                actionId,
                toolName,
                AuditOutcome.Approved,
                startedAt.AddSeconds(1)),
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            ActionEvent(
                runId,
                actionId,
                toolName,
                AuditOutcome.Started,
                startedAt.AddSeconds(2)),
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            ActionEvent(
                runId,
                actionId,
                toolName,
                AuditOutcome.Succeeded,
                startedAt.AddSeconds(3),
                resultCode: "tool_succeeded",
                executionDurationMilliseconds: 1000),
            CancellationToken.None)).IsSuccess);
    }

    private static AuditEventRecord ActionEvent(
        AgentRunId runId,
        string actionId,
        string toolName,
        AuditOutcome outcome,
        DateTimeOffset occurredAt,
        string? resultCode = null,
        long? executionDurationMilliseconds = null)
    {
        var targetIdentity = AgentActionDigest.FromUtf8($"target:{actionId}");
        var targetFingerprint = AgentActionDigest.FromUtf8($"fingerprint:{actionId}");
        return new AuditEventRecord(
            $"{actionId}:{outcome}",
            actionId,
            new ActorDescriptor(
                new ActorId(runId.Value),
                ActorKind.Agent,
                "Agent"),
            toolName,
            new AuditTarget("agent-target-fingerprint", targetFingerprint.Value),
            outcome,
            AuditDetails.ForAgentAction(
                runId,
                Capability(toolName),
                Risk(toolName),
                AgentPermission.Auto,
                AgentPolicyDecision.AuthorizedByAuto,
                AgentActionDigest.FromUtf8($"arguments:{actionId}"),
                outcome == AuditOutcome.Requested
                    ? null
                    : AgentAuthorizationSource.AutoPolicy,
                resultCode: resultCode,
                binding: new AgentActionAuditBinding(
                    policyGeneration: 1,
                    targetIdentity: targetIdentity,
                    executionDurationMilliseconds:
                        executionDurationMilliseconds)),
            occurredAt);
    }

    private static AgentCapability Capability(string toolName) =>
        BuiltInAgentTools.Catalog.TryGet(toolName, out var descriptor)
            ? descriptor!.Capability
            : throw new ArgumentException("The test tool is unknown.", nameof(toolName));

    private static AgentActionRisk Risk(string toolName) =>
        BuiltInAgentTools.Catalog.TryGet(toolName, out var descriptor)
            ? descriptor!.Risk
            : throw new ArgumentException("The test tool is unknown.", nameof(toolName));

    private static T Success<T>(AuditStoreResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }
}
