using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentAuditRecoveryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "lifecycle.restart-outcome-unknown recovers started action exactly once")]
    [Trait("SecurityCampaignCase", "lifecycle.restart-outcome-unknown")]
    public async Task StartedActionsReceiveOneSecretFreeUnknownRestartFailure()
    {
        var started = StartedEvent();
        var store = new RecoveryAuditStore([started]);
        var recovery = new AgentAuditRecovery(store, new FixedTimeProvider(Now));

        var result = await recovery.RecoverAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, result.Value);
        var terminal = Assert.Single(store.Appended);
        Assert.Equal(started.CorrelationId, terminal.CorrelationId);
        Assert.Equal(AuditOutcome.Failed, terminal.Outcome);
        Assert.Equal(ActorKind.System, terminal.Actor.Kind);
        var details = Assert.IsType<AuditDetails.AgentActionDetails>(terminal.Details);
        Assert.Null(details.ErrorCode);
        Assert.Equal(AgentAuditRecovery.RecoveryResultCode, details.ResultCode);
        Assert.DoesNotContain("command text", terminal.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOrAppendFailureIsReturnedWithoutClaimingRecovery()
    {
        var error = new AuditStoreError(
            AuditStoreErrorCode.StorageUnavailable,
            "Unavailable.");
        var readFailure = new RecoveryAuditStore([StartedEvent()])
        {
            ReadError = error,
        };
        var appendFailure = new RecoveryAuditStore([StartedEvent()])
        {
            AppendError = error,
        };

        var first = await new AgentAuditRecovery(
                readFailure,
                new FixedTimeProvider(Now))
            .RecoverAsync(CancellationToken.None);
        var second = await new AgentAuditRecovery(
                appendFailure,
                new FixedTimeProvider(Now))
            .RecoverAsync(CancellationToken.None);

        Assert.False(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Empty(readFailure.Appended);
        Assert.Empty(appendFailure.Appended);
    }

    private static AuditEventRecord StartedEvent() =>
        new(
            "event-started",
            "action-1",
            new ActorDescriptor(new ActorId("agent-1"), ActorKind.Agent, "Agent"),
            BuiltInAgentTools.TerminalSendText,
            new AuditTarget("agent-target-fingerprint", new string('a', 64)),
            AuditOutcome.Started,
            AuditDetails.ForAgentAction(
                new AgentRunId("run-1"),
                AgentCapability.RunCommands,
                AgentActionRisk.Mutation,
                AgentPermission.Ask,
                AgentPolicyDecision.RequiresApproval,
                AgentActionDigest.FromUtf8("command text"),
                AgentAuthorizationSource.HumanApproval),
            Now.AddMinutes(-1));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecoveryAuditStore(
        IReadOnlyList<AuditEventRecord> incomplete) : IAuditStore
    {
        public List<AuditEventRecord> Appended { get; } = [];

        public AuditStoreError? ReadError { get; init; }

        public AuditStoreError? AppendError { get; init; }

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AppendError is not null)
            {
                return ValueTask.FromResult(
                    AuditStoreResult<Unit>.Failure(AppendError));
            }

            Appended.Add(auditEvent);
            return ValueTask.FromResult(AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(
                    []));

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListIncompleteAgentActionsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                ReadError is null
                    ? AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(incomplete)
                    : AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Failure(ReadError));
        }
    }
}
