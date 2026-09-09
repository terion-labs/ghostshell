using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteAuditStoreTests
{
    [Fact]
    public async Task ParallelAgentActionClaimsDoNotCrashTheNativeSqliteProvider()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        const int workerCount = 16;
        const int claimsPerWorker = 128;

        var results = await Task.WhenAll(
            Enumerable.Range(0, workerCount).Select(async worker =>
            {
                var outcomes = new List<AuditStoreResult<AgentActionAuditClaimOutcome>>(
                    claimsPerWorker);
                for (var claim = 0; claim < claimsPerWorker; claim++)
                {
                    var actionId = $"parallel-action-{worker}-{claim}";
                    outcomes.Add(await store.ClaimAgentActionAsync(
                        AgentEvent(
                            $"parallel-event-{worker}-{claim}",
                            actionId,
                            AuditOutcome.Requested),
                        CancellationToken.None));
                }

                return outcomes;
            }));

        var failures = results
            .SelectMany(static worker => worker)
            .Where(static result => !result.IsSuccess)
            .ToArray();
        Assert.Empty(failures);
    }

    [Fact]
    public async Task TypedDetailsRoundTripWithoutExposingJsonToCallers()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var details = AuditDetails.ForSecretAccess(
            SecretUseKind.AiProviderAuthentication,
            SecretVaultErrorCode.AccessDenied);
        var auditEvent = new AuditEventRecord(
            "event-1",
            "correlation-1",
            new ActorDescriptor(new ActorId("system-1"), ActorKind.System, "System"),
            "secret.resolve",
            new AuditTarget("secret", "reference-1"),
            AuditOutcome.Denied,
            details,
            new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));

        var append = await store.AppendAsync(auditEvent, CancellationToken.None);
        var read = await store.ListByCorrelationAsync(
            auditEvent.CorrelationId,
            CancellationToken.None);

        Assert.True(append.IsSuccess, append.Error?.Message);
        Assert.True(read.IsSuccess, read.Error?.Message);
        var stored = Assert.Single(read.Value!);
        Assert.Equal(details, stored.Details);
        Assert.Equal(auditEvent.Action, stored.Action);
        Assert.Equal(auditEvent.Target, stored.Target);
    }

    [Fact]
    public async Task TerminalStartupCommandDetailsRoundTripWithoutCommandText()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var details = AuditDetails.ForTerminalStartupCommands(
            2,
            TerminalStartupCommandDispatchErrorCode.WriteRejected);
        var auditEvent = new AuditEventRecord(
            "event-terminal-startup",
            "correlation-terminal-startup",
            new ActorDescriptor(new ActorId("human-1"), ActorKind.Human, "Local user"),
            TerminalStartupCommandDispatcher.AuditAction,
            new AuditTarget(TerminalStartupCommandDispatcher.AuditTargetKind, "session-1"),
            AuditOutcome.Failed,
            details,
            new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));

        var append = await store.AppendAsync(auditEvent, CancellationToken.None);
        var read = await store.ListByCorrelationAsync(
            auditEvent.CorrelationId,
            CancellationToken.None);

        Assert.True(append.IsSuccess, append.Error?.Message);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(details, Assert.Single(read.Value!).Details);

        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT details_json FROM audit_events WHERE event_id = $eventId;";
        command.Parameters.AddWithValue("$eventId", auditEvent.EventId);
        var encoded = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.DoesNotContain("commandText", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("terminal.write", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentActionDetailsRoundTripWithoutMaterialArguments()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var details = AuditDetails.ForAgentAction(
            new AgentRunId("run-1"),
            AgentCapability.RunCommands,
            AgentActionRisk.Mutation,
            AgentPermission.Ask,
            AgentPolicyDecision.RequiresApproval,
            AgentActionDigest.FromUtf8("secret-canary-command"),
            AgentAuthorizationSource.HumanApproval,
            resultCode: "write_rejected",
            binding: new AgentActionAuditBinding(
                policyGeneration: 7,
                targetIdentity: AgentActionDigest.FromUtf8("target-identity"),
                approvalIdDigest: AgentActionDigest.FromUtf8("approval-id"),
                approvalDuration: AgentApprovalDuration.Once,
                authorizationIdDigest: AgentActionDigest.FromUtf8("authorization-id"),
                authorityExpiresAtUtc:
                    new DateTimeOffset(2026, 7, 23, 12, 1, 0, TimeSpan.Zero),
                executionDurationMilliseconds: 1250,
                resultCount: 1,
                artifactReference: "artifact-1"));
        var auditEvent = new AuditEventRecord(
            "event-agent-action",
            "action-1",
            new ActorDescriptor(new ActorId("agent-1"), ActorKind.Agent, "Agent"),
            BuiltInAgentTools.TerminalSendText,
            new AuditTarget("agent-target-fingerprint", new string('a', 64)),
            AuditOutcome.Failed,
            details,
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero));

        Assert.True(
            (await store.AppendAsync(auditEvent, CancellationToken.None)).IsSuccess);
        var read = await store.ListByCorrelationAsync(
            auditEvent.CorrelationId,
            CancellationToken.None);

        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(details, Assert.Single(read.Value!).Details);
        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT details_json FROM audit_events WHERE event_id = $eventId;";
        command.Parameters.AddWithValue("$eventId", auditEvent.EventId);
        var encoded = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.DoesNotContain("secret-canary-command", encoded, StringComparison.Ordinal);
        Assert.Contains("write_rejected", encoded, StringComparison.Ordinal);
        Assert.Contains("\"policyGeneration\":7", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionOneAgentDetailsRemainReadableWithUnknownBindingsEmpty()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var digest = AgentActionDigest.FromUtf8("legacy-arguments");
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_events(
                    event_id, correlation_id, actor_kind, actor_id, action,
                    target_kind, target_id, outcome, details_json, occurred_utc)
                VALUES (
                    'legacy-agent-event', 'legacy-agent-action', 'Agent', 'agent-1',
                    'terminal.read_screen', 'agent-target-fingerprint', $target,
                    'Started', $details, '2026-07-23T12:00:00.0000000+00:00');
                """;
            command.Parameters.AddWithValue("$target", new string('a', 64));
            command.Parameters.AddWithValue(
                "$details",
                $$"""
                {"schemaVersion":1,"kind":"agent-action","runId":"run-1","capability":"TerminalRead","risk":"Observation","permission":"Auto","decision":"AuthorizedByAuto","argumentDigest":"{{digest.Value}}","authorizationSource":"AutoPolicy","errorCode":null,"resultCode":null}
                """);
            await command.ExecuteNonQueryAsync();
        }

        var result = await store.ListByCorrelationAsync(
            "legacy-agent-action",
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var details = Assert.IsType<AuditDetails.AgentActionDetails>(
            Assert.Single(result.Value!).Details);
        Assert.Equal(AgentActionAuditBinding.Empty, details.Binding);
    }

    [Fact]
    public async Task IncompleteAgentQueryReturnsOnlyCorrelationsWhoseLatestEventIsStarted()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var started = AgentEvent(
            "event-started",
            "action-started",
            AuditOutcome.Started);
        var completedStart = AgentEvent(
            "event-completed-start",
            "action-completed",
            AuditOutcome.Started);
        var completed = AgentEvent(
            "event-completed-terminal",
            "action-completed",
            AuditOutcome.Succeeded);
        var unrelated = new AuditEventRecord(
            "event-unrelated",
            "other-started",
            new ActorDescriptor(new ActorId("system-1"), ActorKind.System, "System"),
            "other.operation",
            null,
            AuditOutcome.Started,
            AuditDetails.None,
            new DateTimeOffset(2026, 7, 23, 12, 0, 3, TimeSpan.Zero));

        Assert.Equal(
            AgentActionAuditClaimOutcome.Claimed,
            (await store.ClaimAgentActionAsync(
                AgentEvent(
                    "event-started-requested",
                    started.CorrelationId,
                    AuditOutcome.Requested),
                CancellationToken.None)).Value);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            AgentEvent(
                "event-started-approved",
                started.CorrelationId,
                AuditOutcome.Approved),
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            started,
            CancellationToken.None)).IsSuccess);
        Assert.Equal(
            AgentActionAuditClaimOutcome.Claimed,
            (await store.ClaimAgentActionAsync(
                AgentEvent(
                    "event-completed-requested",
                    completed.CorrelationId,
                    AuditOutcome.Requested),
                CancellationToken.None)).Value);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            AgentEvent(
                "event-completed-approved",
                completed.CorrelationId,
                AuditOutcome.Approved),
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            completedStart,
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            completed,
            CancellationToken.None)).IsSuccess);
        Assert.True(
            (await store.AppendAsync(unrelated, CancellationToken.None)).IsSuccess);

        var result = await store.ListIncompleteAgentActionsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(started, Assert.Single(result.Value!));
    }

    [Fact]
    public async Task AgentActionStateMakesClaimsAndPhaseRetriesDurablyIdempotent()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var requested = AgentEvent(
            "phase-requested",
            "durable-action",
            AuditOutcome.Requested);
        var approved = AgentEvent(
            "phase-approved",
            requested.CorrelationId,
            AuditOutcome.Approved);
        var started = AgentEvent(
            "phase-started",
            requested.CorrelationId,
            AuditOutcome.Started);
        var succeeded = AgentEvent(
            "phase-succeeded",
            requested.CorrelationId,
            AuditOutcome.Succeeded);
        var conflicting = AgentEvent(
            "phase-failed",
            requested.CorrelationId,
            AuditOutcome.Failed);

        var claim = await store.ClaimAgentActionAsync(
            requested,
            CancellationToken.None);
        var duplicate = await store.ClaimAgentActionAsync(
            requested with { EventId = "another-requested-event" },
            CancellationToken.None);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            approved,
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            approved,
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            started,
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            succeeded,
            CancellationToken.None)).IsSuccess);
        var conflict = await store.AppendAgentActionPhaseAsync(
            conflicting,
            CancellationToken.None);

        Assert.Equal(AgentActionAuditClaimOutcome.Claimed, claim.Value);
        Assert.Equal(AgentActionAuditClaimOutcome.AlreadyClaimed, duplicate.Value);
        Assert.False(conflict.IsSuccess);
        Assert.Equal(AuditStoreErrorCode.Conflict, conflict.Error!.Code);
        var trail = await store.ListByCorrelationAsync(
            requested.CorrelationId,
            CancellationToken.None);
        Assert.Equal(4, trail.Value!.Count);
    }

    [Fact]
    public async Task StartupRecoveryAdvancesDurableStartedStateExactlyOnce()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var actionId = "interrupted-action";
        Assert.Equal(
            AgentActionAuditClaimOutcome.Claimed,
            (await store.ClaimAgentActionAsync(
                AgentEvent("recovery-requested", actionId, AuditOutcome.Requested),
                CancellationToken.None)).Value);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            AgentEvent("recovery-approved", actionId, AuditOutcome.Approved),
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            AgentEvent("recovery-started", actionId, AuditOutcome.Started),
            CancellationToken.None)).IsSuccess);
        var recovery = new AgentAuditRecovery(
            store,
            new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 23, 12, 5, 0, TimeSpan.Zero)));

        var first = await recovery.RecoverAsync(CancellationToken.None);
        var second = await recovery.RecoverAsync(CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(1, first.Value);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(0, second.Value);
        var trail = await store.ListByCorrelationAsync(actionId, CancellationToken.None);
        Assert.Equal(
            [
                AuditOutcome.Requested,
                AuditOutcome.Approved,
                AuditOutcome.Started,
                AuditOutcome.Failed,
            ],
            trail.Value!.Select(item => item.Outcome));
        var recovered = Assert.IsType<AuditDetails.AgentActionDetails>(
            trail.Value![^1].Details);
        Assert.Null(recovered.ErrorCode);
        Assert.Equal(
            "application_restart_outcome_unknown",
            recovered.ResultCode);
    }

    [Fact]
    public async Task IncompleteAgentQueryFailsClosedWhenStartedStateLosesItsEvent()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var actionId = "tampered-started-action";
        Assert.Equal(
            AgentActionAuditClaimOutcome.Claimed,
            (await store.ClaimAgentActionAsync(
                AgentEvent("tamper-requested", actionId, AuditOutcome.Requested),
                CancellationToken.None)).Value);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            AgentEvent("tamper-approved", actionId, AuditOutcome.Approved),
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.AppendAgentActionPhaseAsync(
            AgentEvent("tamper-started", actionId, AuditOutcome.Started),
            CancellationToken.None)).IsSuccess);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_action_audit_state
                SET last_event_id = 'missing-started-event'
                WHERE action_id = $actionId;
                """;
            command.Parameters.AddWithValue("$actionId", actionId);
            await command.ExecuteNonQueryAsync();
        }

        var result = await store.ListIncompleteAgentActionsAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuditStoreErrorCode.StorageFailure, result.Error!.Code);
    }

    [Fact]
    public async Task UnknownDetailFieldsAreRejectedWhenReadingTheTrail()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        var auditEvent = new AuditEventRecord(
            "event-2",
            "correlation-2",
            new ActorDescriptor(new ActorId("system-1"), ActorKind.System, "System"),
            "secret.resolve",
            null,
            AuditOutcome.Started,
            AuditDetails.None,
            DateTimeOffset.UtcNow);
        Assert.True(
            (await store.AppendAsync(auditEvent, CancellationToken.None)).IsSuccess);

        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE audit_events
                SET details_json = '{"schemaVersion":1,"kind":"none","password":"canary"}'
                WHERE event_id = 'event-2';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var read = await store.ListByCorrelationAsync(
            auditEvent.CorrelationId,
            CancellationToken.None);

        Assert.False(read.IsSuccess);
        Assert.Equal(AuditStoreErrorCode.StorageFailure, read.Error!.Code);
    }

    [Fact]
    public async Task ProfileLockFailureReturnsATypedStorageError()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using var competingDatabase = new AsuraDatabase(
            new SqliteStorageOptions(temporary.DatabasePath),
            TimeProvider.System);
        var store = new SqliteAuditStore(competingDatabase);
        var auditEvent = new AuditEventRecord(
            "event-3",
            "correlation-3",
            new ActorDescriptor(new ActorId("system-1"), ActorKind.System, "System"),
            "secret.resolve",
            null,
            AuditOutcome.Started,
            AuditDetails.None,
            DateTimeOffset.UtcNow);

        var append = await store.AppendAsync(auditEvent, CancellationToken.None);

        Assert.False(append.IsSuccess);
        Assert.Equal(AuditStoreErrorCode.StorageUnavailable, append.Error!.Code);
    }

    private static AuditEventRecord AgentEvent(
        string eventId,
        string correlationId,
        AuditOutcome outcome) =>
        new(
            eventId,
            correlationId,
            new ActorDescriptor(new ActorId("agent-1"), ActorKind.Agent, "Agent"),
            BuiltInAgentTools.TerminalReadScreen,
            new AuditTarget("agent-target-fingerprint", new string('a', 64)),
            outcome,
            AuditDetails.ForAgentAction(
                new AgentRunId("run-1"),
                AgentCapability.TerminalRead,
                AgentActionRisk.Observation,
                AgentPermission.Auto,
                AgentPolicyDecision.AuthorizedByAuto,
                AgentActionDigest.FromUtf8(correlationId),
                AgentAuthorizationSource.AutoPolicy),
            new DateTimeOffset(
                2026,
                7,
                23,
                12,
                0,
                eventId.Contains("terminal", StringComparison.Ordinal) ? 2 : 1,
                TimeSpan.Zero));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
