using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Reconciles actions that durably started but could not record a terminal
/// outcome before the previous process stopped.
/// </summary>
public sealed class AgentAuditRecovery
{
    public const string RecoveryResultCode =
        "application_restart_outcome_unknown";
    private readonly IAuditStore _auditStore;
    private readonly TimeProvider _timeProvider;

    public AgentAuditRecovery(
        IAuditStore auditStore,
        TimeProvider timeProvider)
    {
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<AuditStoreResult<int>> RecoverAsync(
        CancellationToken cancellationToken)
    {
        var incomplete = await _auditStore
            .ListIncompleteAgentActionsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!incomplete.IsSuccess)
        {
            return AuditStoreResult<int>.Failure(incomplete.Error!);
        }

        var recovered = 0;
        foreach (var started in incomplete.Value!)
        {
            if (started.Outcome != AuditOutcome.Started
                || started.Details is not AuditDetails.AgentActionDetails details)
            {
                return AuditStoreResult<int>.Failure(new AuditStoreError(
                    AuditStoreErrorCode.StorageFailure,
                    "The incomplete agent-action audit query returned an invalid event."));
            }

            var terminal = new AuditEventRecord(
                AgentAuditEventId.ForPhase(
                    new AgentActionId(started.CorrelationId),
                    AuditOutcome.Failed),
                started.CorrelationId,
                SystemActor(),
                started.Action,
                started.Target,
                AuditOutcome.Failed,
                AuditDetails.ForAgentAction(
                    details.RunId,
                    details.Capability,
                    details.Risk,
                    details.Permission,
                    details.Decision,
                    details.ArgumentDigest,
                    details.AuthorizationSource,
                    errorCode: null,
                    resultCode: RecoveryResultCode,
                    binding: details.Binding.WithExecutionDuration(
                        _timeProvider.GetUtcNow() - started.OccurredAt)),
                _timeProvider.GetUtcNow());
            var append = await _auditStore
                .AppendAgentActionPhaseAsync(terminal, cancellationToken)
                .ConfigureAwait(false);
            if (!append.IsSuccess)
            {
                return AuditStoreResult<int>.Failure(append.Error!);
            }

            recovered++;
        }

        return AuditStoreResult<int>.Success(recovered);
    }

    private static ActorDescriptor SystemActor() =>
        new(
            new ActorId("agent-audit-recovery"),
            ActorKind.System,
            "Agent audit recovery");
}
