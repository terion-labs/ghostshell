using System.Globalization;
using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

public sealed class SqliteAuditStore : IAuditStore
{
    private readonly AsuraDatabase _database;

    public SqliteAuditStore(AsuraDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async ValueTask<AuditStoreResult<Unit>> AppendAsync(
        AuditEventRecord auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (!TryValidate(auditEvent))
        {
            return Failure<Unit>(
                AuditStoreErrorCode.InvalidEvent,
                "The audit event has invalid metadata.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await InsertEventAsync(
                    connection,
                    transaction: null,
                    auditEvent,
                    cancellationToken)
                .ConfigureAwait(false);
            return AuditStoreResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(AuditStoreErrorCode.Cancelled, "Writing the audit event was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<Unit>(
                MapSqliteError(exception),
                "The audit store could not persist the event.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<Unit>(
                AuditStoreErrorCode.StorageUnavailable,
                "The audit store is unavailable.");
        }
    }

    public async ValueTask<AuditStoreResult<AgentActionAuditClaimOutcome>>
        ClaimAgentActionAsync(
            AuditEventRecord requestedEvent,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedEvent);
        if (requestedEvent.Outcome != AuditOutcome.Requested
            || !TryValidate(requestedEvent))
        {
            return Failure<AgentActionAuditClaimOutcome>(
                AuditStoreErrorCode.InvalidEvent,
                "An agent action claim requires a valid Requested event.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using var claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = """
                INSERT INTO agent_action_audit_state(
                    action_id, phase, last_event_id, updated_utc)
                VALUES ($actionId, 'Requested', $eventId, $updatedUtc)
                ON CONFLICT(action_id) DO NOTHING;
                SELECT changes();
                """;
            claim.Parameters.AddWithValue("$actionId", requestedEvent.CorrelationId);
            claim.Parameters.AddWithValue("$eventId", requestedEvent.EventId);
            claim.Parameters.AddWithValue(
                "$updatedUtc",
                requestedEvent.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
            var changed = Convert.ToInt64(
                await claim.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (changed == 0)
            {
                return AuditStoreResult<AgentActionAuditClaimOutcome>.Success(
                    AgentActionAuditClaimOutcome.AlreadyClaimed);
            }

            await InsertEventAsync(
                    connection,
                    transaction,
                    requestedEvent,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AuditStoreResult<AgentActionAuditClaimOutcome>.Success(
                AgentActionAuditClaimOutcome.Claimed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<AgentActionAuditClaimOutcome>(
                AuditStoreErrorCode.Cancelled,
                "Claiming the agent action was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<AgentActionAuditClaimOutcome>(
                MapSqliteError(exception),
                "The audit store could not claim the agent action.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<AgentActionAuditClaimOutcome>(
                AuditStoreErrorCode.StorageUnavailable,
                "The audit store is unavailable.");
        }
    }

    public async ValueTask<AuditStoreResult<Unit>> AppendAgentActionPhaseAsync(
        AuditEventRecord auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (auditEvent.Outcome == AuditOutcome.Requested
            || !TryValidate(auditEvent))
        {
            return Failure<Unit>(
                AuditStoreErrorCode.InvalidEvent,
                "An agent action phase requires a valid non-Requested event.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var current = await ReadAgentActionStateAsync(
                    connection,
                    transaction,
                    auditEvent.CorrelationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                return Conflict("The agent action has no durable Requested phase.");
            }

            if (string.Equals(
                    current.LastEventId,
                    auditEvent.EventId,
                    StringComparison.Ordinal)
                && current.Phase == auditEvent.Outcome)
            {
                return AuditStoreResult<Unit>.Success(Unit.Value);
            }

            if (!CanTransition(current.Phase, auditEvent.Outcome))
            {
                return Conflict(
                    "The durable agent action already has a conflicting phase.");
            }

            await InsertEventAsync(
                    connection,
                    transaction,
                    auditEvent,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE agent_action_audit_state
                SET phase = $phase,
                    last_event_id = $eventId,
                    updated_utc = $updatedUtc
                WHERE action_id = $actionId
                  AND phase = $expectedPhase;
                """;
            update.Parameters.AddWithValue("$phase", auditEvent.Outcome.ToString());
            update.Parameters.AddWithValue("$eventId", auditEvent.EventId);
            update.Parameters.AddWithValue(
                "$updatedUtc",
                auditEvent.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$actionId", auditEvent.CorrelationId);
            update.Parameters.AddWithValue("$expectedPhase", current.Phase.ToString());
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Conflict("The agent action phase changed concurrently.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AuditStoreResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(
                AuditStoreErrorCode.Cancelled,
                "Writing the agent action phase was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<Unit>(
                MapSqliteError(exception),
                "The audit store could not persist the agent action phase.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<Unit>(
                AuditStoreErrorCode.StorageUnavailable,
                "The audit store is unavailable.");
        }
    }

    public async ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>> ListByCorrelationAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_id, actor_kind, actor_id, action, target_kind, target_id,
                       outcome, details_json, occurred_utc
                FROM audit_events
                WHERE correlation_id = $correlationId
                ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$correlationId", correlationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var events = new List<AuditEventRecord>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!TryReadEvent(reader, correlationId, out var auditEvent))
                {
                    return CorruptTrail();
                }

                events.Add(auditEvent!);
            }

            return AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(events);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<IReadOnlyList<AuditEventRecord>>(
                AuditStoreErrorCode.Cancelled,
                "Reading the audit trail was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<IReadOnlyList<AuditEventRecord>>(
                MapSqliteError(exception),
                "The audit store could not read the trail.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<IReadOnlyList<AuditEventRecord>>(
                AuditStoreErrorCode.StorageUnavailable,
                "The audit store is unavailable.");
        }
    }

    public async ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
        ListIncompleteAgentActionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event.event_id, event.actor_kind, event.actor_id, event.action,
                       event.target_kind, event.target_id, event.outcome,
                       event.details_json, event.occurred_utc, state.action_id,
                       event.correlation_id
                FROM agent_action_audit_state AS state
                LEFT JOIN audit_events AS event
                    ON event.event_id = state.last_event_id
                WHERE state.phase = 'Started'
                ORDER BY state.updated_utc, state.action_id
                LIMIT 512;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var events = new List<AuditEventRecord>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var actionId = reader.GetString(9);
                if (Enumerable.Range(0, 9).Any(reader.IsDBNull)
                    || reader.IsDBNull(10)
                    || !string.Equals(
                        actionId,
                        reader.GetString(10),
                        StringComparison.Ordinal)
                    || !TryReadEvent(reader, actionId, out var auditEvent)
                    || auditEvent!.Outcome != AuditOutcome.Started
                    || auditEvent.Details is not AuditDetails.AgentActionDetails)
                {
                    return CorruptTrail();
                }

                events.Add(auditEvent!);
            }

            return AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(events);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<IReadOnlyList<AuditEventRecord>>(
                AuditStoreErrorCode.Cancelled,
                "Reading incomplete agent actions was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<IReadOnlyList<AuditEventRecord>>(
                MapSqliteError(exception),
                "The audit store could not inspect incomplete agent actions.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<IReadOnlyList<AuditEventRecord>>(
                AuditStoreErrorCode.StorageUnavailable,
                "The audit store is unavailable.");
        }
    }

    private static AuditStoreResult<T> Failure<T>(AuditStoreErrorCode code, string message) =>
        AuditStoreResult<T>.Failure(new AuditStoreError(code, message));

    private static AuditStoreResult<Unit> Conflict(string message) =>
        Failure<Unit>(AuditStoreErrorCode.Conflict, message);

    private static async ValueTask InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AuditEventRecord auditEvent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events(
                event_id, correlation_id, actor_kind, actor_id, action,
                target_kind, target_id, outcome, details_json, occurred_utc)
            VALUES (
                $eventId, $correlationId, $actorKind, $actorId, $action,
                $targetKind, $targetId, $outcome, $detailsJson, $occurredUtc);
            """;
        command.Parameters.AddWithValue("$eventId", auditEvent.EventId);
        command.Parameters.AddWithValue("$correlationId", auditEvent.CorrelationId);
        command.Parameters.AddWithValue("$actorKind", auditEvent.Actor.Kind.ToString());
        command.Parameters.AddWithValue("$actorId", auditEvent.Actor.Id.Value);
        command.Parameters.AddWithValue("$action", auditEvent.Action);
        command.Parameters.AddWithValue(
            "$targetKind",
            (object?)auditEvent.Target?.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$targetId",
            (object?)auditEvent.Target?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", auditEvent.Outcome.ToString());
        command.Parameters.AddWithValue(
            "$detailsJson",
            AuditDetailsJson.Serialize(auditEvent.Details));
        command.Parameters.AddWithValue(
            "$occurredUtc",
            auditEvent.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<AgentActionState?> ReadAgentActionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT phase, last_event_id
            FROM agent_action_audit_state
            WHERE action_id = $actionId;
            """;
        command.Parameters.AddWithValue("$actionId", actionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return TryParseEnum(reader.GetString(0), out AuditOutcome phase)
            && !string.IsNullOrWhiteSpace(reader.GetString(1))
                ? new AgentActionState(phase, reader.GetString(1))
                : throw new InvalidOperationException(
                    "The durable agent action state is invalid.");
    }

    private static bool CanTransition(AuditOutcome current, AuditOutcome next) =>
        current switch
        {
            AuditOutcome.Requested =>
                next is AuditOutcome.Approved or AuditOutcome.Denied,
            AuditOutcome.Approved =>
                next is AuditOutcome.Started or AuditOutcome.Denied,
            AuditOutcome.Started =>
                next is AuditOutcome.Succeeded
                    or AuditOutcome.Failed
                    or AuditOutcome.Cancelled,
            _ => false,
        };

    internal static bool TryReadEvent(
        SqliteDataReader reader,
        string correlationId,
        out AuditEventRecord? auditEvent)
    {
        auditEvent = null;
        var eventId = reader.GetString(0);
        var actorId = reader.GetString(2);
        var action = reader.GetString(3);
        if (!TryParseEnum(reader.GetString(1), out ActorKind actorKind)
            || !TryParseEnum(reader.GetString(6), out AuditOutcome outcome)
            || !AuditDetailsJson.TryDeserialize(reader.GetString(7), out var details)
            || string.IsNullOrWhiteSpace(eventId)
            || string.IsNullOrWhiteSpace(correlationId)
            || string.IsNullOrWhiteSpace(actorId)
            || string.IsNullOrWhiteSpace(action)
            || !DateTimeOffset.TryParse(
                reader.GetString(8),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var occurredAt))
        {
            return false;
        }

        var hasTargetKind = !reader.IsDBNull(4);
        var hasTargetId = !reader.IsDBNull(5);
        if (hasTargetKind != hasTargetId)
        {
            return false;
        }

        var target = reader.IsDBNull(4)
            ? null
            : new AuditTarget(reader.GetString(4), reader.GetString(5));
        auditEvent = new AuditEventRecord(
            eventId,
            correlationId,
            new ActorDescriptor(
                new ActorId(actorId),
                actorKind,
                actorKind.ToString()),
            action,
            target,
            outcome,
            details!,
            occurredAt);
        return TryValidate(auditEvent);
    }

    private static bool TryValidate(AuditEventRecord auditEvent) =>
        !string.IsNullOrWhiteSpace(auditEvent.EventId)
        && !string.IsNullOrWhiteSpace(auditEvent.CorrelationId)
        && auditEvent.Actor is not null
        && !string.IsNullOrWhiteSpace(auditEvent.Actor.Id.Value)
        && Enum.IsDefined(auditEvent.Actor.Kind)
        && !string.IsNullOrWhiteSpace(auditEvent.Action)
        && (auditEvent.Target is null
            || (!string.IsNullOrWhiteSpace(auditEvent.Target.Kind)
                && !string.IsNullOrWhiteSpace(auditEvent.Target.Id)))
        && Enum.IsDefined(auditEvent.Outcome)
        && auditEvent.Details is not null;

    private static bool TryParseEnum<T>(string text, out T value)
        where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: false, out value)
        && Enum.IsDefined(value)
        && string.Equals(value.ToString(), text, StringComparison.Ordinal);

    private static AuditStoreResult<IReadOnlyList<AuditEventRecord>> CorruptTrail() =>
        Failure<IReadOnlyList<AuditEventRecord>>(
            AuditStoreErrorCode.StorageFailure,
            "The audit store contains an invalid event.");

    private static AuditStoreErrorCode MapSqliteError(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? AuditStoreErrorCode.StorageUnavailable
            : AuditStoreErrorCode.StorageFailure;

    private static bool IsStorageBoundaryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException;

    private sealed record AgentActionState(
        AuditOutcome Phase,
        string LastEventId);
}
