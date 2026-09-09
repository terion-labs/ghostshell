namespace Asura.Application;

public interface IAuditStore
{
    ValueTask<AuditStoreResult<Unit>> AppendAsync(
        AuditEventRecord auditEvent,
        CancellationToken cancellationToken);

    ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>> ListByCorrelationAsync(
        string correlationId,
        CancellationToken cancellationToken);

    async ValueTask<AuditStoreResult<AgentActionAuditClaimOutcome>>
        ClaimAgentActionAsync(
            AuditEventRecord requestedEvent,
            CancellationToken cancellationToken)
    {
        var append = await AppendAsync(requestedEvent, cancellationToken)
            .ConfigureAwait(false);
        return append.IsSuccess
            ? AuditStoreResult<AgentActionAuditClaimOutcome>.Success(
                AgentActionAuditClaimOutcome.Claimed)
            : AuditStoreResult<AgentActionAuditClaimOutcome>.Failure(append.Error!);
    }

    ValueTask<AuditStoreResult<Unit>> AppendAgentActionPhaseAsync(
        AuditEventRecord auditEvent,
        CancellationToken cancellationToken) =>
        AppendAsync(auditEvent, cancellationToken);

    /// <summary>
    /// Returns agent actions whose latest durable event is <see cref="AuditOutcome.Started"/>.
    /// Stores that do not persist agent audit trails may keep the fail-closed
    /// empty default; the desktop SQLite store implements the recovery query.
    /// </summary>
    ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
        ListIncompleteAgentActionsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(
                []));
}
