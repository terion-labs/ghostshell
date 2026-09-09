using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Delivers one saved-screen startup batch through the leased terminal input boundary. Audit
/// persistence is checked before delivery and every attempted delivery receives a correlated
/// terminal-specific outcome without storing command text.
/// </summary>
public sealed class TerminalStartupCommandDispatcher
{
    public const string AuditAction = "terminal.startup_commands";
    public const string AuditTargetKind = "terminal-session";

    private readonly IAuditStore _auditStore;
    private readonly TimeProvider _timeProvider;

    public TerminalStartupCommandDispatcher(
        IAuditStore auditStore,
        TimeProvider timeProvider)
    {
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<TerminalStartupCommandDispatchResult> DispatchAsync(
        ISessionHostClient sessionClient,
        SessionId sessionId,
        InputLeaseId leaseId,
        IReadOnlyList<string> commands,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionClient);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(context);

        var batch = Normalize(commands);
        if (batch.Count == 0)
        {
            throw new ArgumentException(
                "A startup-command dispatch requires at least one non-empty command.",
                nameof(commands));
        }

        var correlationId = context.RequestId.Value;
        var started = await AppendAuditAsync(
                sessionId,
                context,
                correlationId,
                batch.Count,
                AuditOutcome.Started,
                errorCode: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            if (started.Error?.Code == AuditStoreErrorCode.Cancelled
                || cancellationToken.IsCancellationRequested)
            {
                return await CompleteCancelledAsync(
                        sessionId,
                        context,
                        correlationId,
                        batch.Count)
                    .ConfigureAwait(false);
            }

            return AuditFailure(commandsDelivered: false);
        }

        HostResult<Unit> writeResult;
        try
        {
            writeResult = await sessionClient.WriteTerminalAsync(
                    new TerminalWriteRequest(
                        sessionId,
                        leaseId,
                        string.Join('\n', batch) + "\n"),
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteCancelledAsync(
                    sessionId,
                    context,
                    correlationId,
                    batch.Count)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await CompleteFailureAsync(
                    sessionId,
                    context,
                    correlationId,
                    batch.Count,
                    TerminalStartupCommandDispatchErrorCode.WriteOutcomeUnknown,
                    "The terminal did not acknowledge the saved startup commands. Asura will retry the same idempotent batch.",
                    retryable: true)
                .ConfigureAwait(false);
        }

        if (writeResult is HostResult<Unit>.Failure failure)
        {
            if (failure.Error.Code == HostErrorCode.Cancelled)
            {
                return await CompleteCancelledAsync(
                        sessionId,
                        context,
                        correlationId,
                        batch.Count)
                    .ConfigureAwait(false);
            }

            return await CompleteFailureAsync(
                    sessionId,
                    context,
                    correlationId,
                    batch.Count,
                    TerminalStartupCommandDispatchErrorCode.WriteRejected,
                    "The live terminal rejected the saved startup commands.",
                    failure.Error.Retryable)
                .ConfigureAwait(false);
        }

        var completed = await AppendAuditAsync(
                sessionId,
                context,
                correlationId,
                batch.Count,
                AuditOutcome.Succeeded,
                errorCode: null,
                CancellationToken.None)
            .ConfigureAwait(false);
        return completed.IsSuccess
            ? TerminalStartupCommandDispatchResult.Success()
            : AuditFailure(commandsDelivered: true);
    }

    private async ValueTask<TerminalStartupCommandDispatchResult> CompleteFailureAsync(
        SessionId sessionId,
        OperationContext context,
        string correlationId,
        int commandCount,
        TerminalStartupCommandDispatchErrorCode errorCode,
        string message,
        bool retryable)
    {
        var audit = await AppendAuditAsync(
                sessionId,
                context,
                correlationId,
                commandCount,
                AuditOutcome.Failed,
                errorCode,
                CancellationToken.None)
            .ConfigureAwait(false);
        return audit.IsSuccess
            ? TerminalStartupCommandDispatchResult.Failure(
                new TerminalStartupCommandDispatchError(errorCode, message, retryable))
            : AuditFailure(commandsDelivered: false);
    }

    private async ValueTask<TerminalStartupCommandDispatchResult> CompleteCancelledAsync(
        SessionId sessionId,
        OperationContext context,
        string correlationId,
        int commandCount)
    {
        var audit = await AppendAuditAsync(
                sessionId,
                context,
                correlationId,
                commandCount,
                AuditOutcome.Cancelled,
                TerminalStartupCommandDispatchErrorCode.Cancelled,
                CancellationToken.None)
            .ConfigureAwait(false);
        return audit.IsSuccess
            ? TerminalStartupCommandDispatchResult.Failure(
                new TerminalStartupCommandDispatchError(
                    TerminalStartupCommandDispatchErrorCode.Cancelled,
                    "Sending the saved startup commands was cancelled.",
                    Retryable: true))
            : AuditFailure(commandsDelivered: false);
    }

    private async ValueTask<AuditStoreResult<Unit>> AppendAuditAsync(
        SessionId sessionId,
        OperationContext context,
        string correlationId,
        int commandCount,
        AuditOutcome outcome,
        TerminalStartupCommandDispatchErrorCode? errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _auditStore.AppendAsync(
                    new AuditEventRecord(
                        Guid.NewGuid().ToString("N"),
                        correlationId,
                        context.Actor,
                        AuditAction,
                        new AuditTarget(AuditTargetKind, sessionId.Value),
                        outcome,
                        AuditDetails.ForTerminalStartupCommands(commandCount, errorCode),
                        _timeProvider.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuditStoreResult<Unit>.Failure(new AuditStoreError(
                AuditStoreErrorCode.Cancelled,
                "Writing the startup-command audit event was cancelled."));
        }
        catch (Exception)
        {
            return AuditStoreResult<Unit>.Failure(new AuditStoreError(
                AuditStoreErrorCode.StorageUnavailable,
                "The startup-command audit trail is unavailable."));
        }
    }

    private static TerminalStartupCommandDispatchResult AuditFailure(bool commandsDelivered) =>
        TerminalStartupCommandDispatchResult.Failure(
            new TerminalStartupCommandDispatchError(
                TerminalStartupCommandDispatchErrorCode.AuditPersistenceFailure,
                commandsDelivered
                    ? "The startup commands were sent, but their audit outcome could not be persisted. They will not be sent again."
                    : "The startup commands were not sent because the audit trail is unavailable.",
                Retryable: !commandsDelivered),
            commandsDelivered);

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string> commands) =>
        Array.AsReadOnly(commands
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(command => command.TrimEnd('\r', '\n'))
            .ToArray());
}
