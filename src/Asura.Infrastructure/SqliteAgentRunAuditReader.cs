using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

/// <summary>
/// Projects the append-only audit log into bounded action timelines. The
/// projection rejects a whole page when any selected entry is malformed or
/// internally inconsistent, so presentation cannot turn corrupt evidence into
/// a plausible success.
/// </summary>
public sealed class SqliteAgentRunAuditReader : IAgentRunAuditReader
{
    private const string AgentActionKind = "agent-action";
    private const string AgentPolicyKind = "agent-run-policy-transition";
    private const string AgentTargetKind = "agent-target-fingerprint";
    private const string PolicyAction = "agent.run.policy";
    private const int MaximumStoredEventsPerEntry = 8;
    // An action's newest sequence advances as phases append. The cursor binds
    // both a read high-water and a page boundary so an unseen action cannot
    // move across that boundary and disappear between page requests.
    private const string CursorVersion = "2";

    private readonly AsuraDatabase _database;

    public SqliteAgentRunAuditReader(AsuraDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<AuditStoreResult<AgentRunAuditPage>> ReadAsync(
        AgentRunAuditQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!TryDecodeCursor(query, out var cursor))
        {
            return Failure(
                AuditStoreErrorCode.InvalidQuery,
                "The agent-run audit cursor does not belong to this run.");
        }

        try
        {
            await using var connection = await _database
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var storedPage = await ReadStoredEntriesAsync(
                    connection,
                    query,
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);
            if (storedPage is null)
            {
                return CorruptTrail();
            }

            var storedEntries = storedPage.Entries;
            var hasMore = storedEntries.Count > query.PageSize;
            var selected = hasMore
                ? storedEntries.Take(query.PageSize).ToArray()
                : [.. storedEntries];
            var entries = new List<AgentRunAuditEntry>(selected.Length);
            foreach (var stored in selected)
            {
                var parsed = ParseEntry(query.RunId, stored);
                if (parsed is null)
                {
                    return CorruptTrail();
                }

                entries.Add(parsed);
            }

            var next = hasMore
                ? EncodeCursor(
                    query.RunId,
                    storedPage.SnapshotSequence,
                    selected[^1].LatestSequence)
                : null;
            return AuditStoreResult<AgentRunAuditPage>.Success(
                new AgentRunAuditPage(entries, next));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                AuditStoreErrorCode.Cancelled,
                "Reading the agent-run audit trail was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure(
                MapSqliteError(exception),
                "The agent-run audit trail could not be read.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure(
                AuditStoreErrorCode.StorageUnavailable,
                "The agent-run audit store is unavailable.");
        }
    }

    private static async ValueTask<StoredPage?>
        ReadStoredEntriesAsync(
            SqliteConnection connection,
            AgentRunAuditQuery query,
            CursorPosition? cursor,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH snapshot AS (
                SELECT COALESCE(
                    $snapshotSequence,
                    (
                        SELECT MAX(sequence)
                        FROM audit_events
                        WHERE json_extract(details_json, '$.runId') = $runId
                          AND json_extract(details_json, '$.kind') IN (
                                'agent-action',
                                'agent-run-policy-transition')
                    )) AS sequence
            ),
            run_entries AS (
                SELECT
                    CASE json_extract(audit_events.details_json, '$.kind')
                        WHEN 'agent-action' THEN audit_events.correlation_id
                        ELSE audit_events.event_id
                    END AS entry_key,
                    json_extract(audit_events.details_json, '$.kind') AS entry_kind,
                    MAX(audit_events.sequence) AS latest_sequence
                FROM audit_events
                CROSS JOIN snapshot
                WHERE json_extract(audit_events.details_json, '$.runId') = $runId
                  AND json_extract(audit_events.details_json, '$.kind') IN (
                        'agent-action',
                        'agent-run-policy-transition')
                  AND audit_events.sequence <= snapshot.sequence
                GROUP BY entry_key, entry_kind
                HAVING $beforeSequence IS NULL
                    OR MAX(audit_events.sequence) < $beforeSequence
                ORDER BY latest_sequence DESC
                LIMIT $entryLimit
            )
            SELECT
                event.event_id,
                event.actor_kind,
                event.actor_id,
                event.action,
                event.target_kind,
                event.target_id,
                event.outcome,
                event.details_json,
                event.occurred_utc,
                event.correlation_id,
                event.sequence,
                selected.entry_key,
                selected.entry_kind,
                selected.latest_sequence,
                snapshot.sequence
            FROM run_entries AS selected
            CROSS JOIN snapshot
            INNER JOIN audit_events AS event
                ON ((
                        selected.entry_kind = 'agent-action'
                        AND event.correlation_id = selected.entry_key
                        AND json_extract(event.details_json, '$.kind') = 'agent-action'
                        AND json_extract(event.details_json, '$.runId') = $runId)
                    OR (
                        selected.entry_kind = 'agent-run-policy-transition'
                        AND event.event_id = selected.entry_key
                        AND json_extract(event.details_json, '$.kind')
                            = 'agent-run-policy-transition'
                        AND json_extract(event.details_json, '$.runId') = $runId))
                  AND event.sequence <= snapshot.sequence
            ORDER BY selected.latest_sequence DESC, event.sequence;
            """;
        command.Parameters.AddWithValue("$runId", query.RunId.Value);
        command.Parameters.AddWithValue(
            "$snapshotSequence",
            cursor is not null
                ? cursor.SnapshotSequence
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$beforeSequence",
            cursor is not null
                ? cursor.BeforeSequence
                : DBNull.Value);
        command.Parameters.AddWithValue("$entryLimit", query.PageSize + 1);

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var entries = new List<StoredEntry>(query.PageSize + 1);
        long? snapshotSequence = null;
        StoredEntry? current = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var correlationId = reader.GetString(9);
            var sequence = reader.GetInt64(10);
            var entryKey = reader.GetString(11);
            var entryKind = reader.GetString(12);
            var latestSequence = reader.GetInt64(13);
            var rowSnapshotSequence = reader.GetInt64(14);
            if (!SqliteAuditStore.TryReadEvent(
                    reader,
                    correlationId,
                    out var auditEvent)
                || auditEvent is null
                || sequence < 1
                || latestSequence < sequence
                || rowSnapshotSequence < latestSequence
                || entryKind is not (AgentActionKind or AgentPolicyKind))
            {
                return null;
            }

            if (!MergeExact(ref snapshotSequence, rowSnapshotSequence))
            {
                return null;
            }

            if (current is null
                || !string.Equals(
                    current.EntryKey,
                    entryKey,
                    StringComparison.Ordinal))
            {
                current = new StoredEntry(entryKey, entryKind, latestSequence);
                entries.Add(current);
            }
            else if (!string.Equals(
                         current.Kind,
                         entryKind,
                         StringComparison.Ordinal)
                     || current.LatestSequence != latestSequence)
            {
                return null;
            }

            if (current.Events.Count == MaximumStoredEventsPerEntry)
            {
                return null;
            }

            current.Events.Add(new StoredEvent(sequence, auditEvent));
        }

        if (entries.Any(entry =>
                entry.Events.Count == 0
                || entry.Events[^1].Sequence != entry.LatestSequence))
        {
            return null;
        }

        return new StoredPage(
            entries,
            snapshotSequence ?? cursor?.SnapshotSequence ?? 0);
    }

    private static AgentRunAuditEntry? ParseEntry(
        AgentRunId runId,
        StoredEntry stored) =>
        stored.Kind switch
        {
            AgentActionKind => ParseAction(runId, stored),
            AgentPolicyKind => ParsePolicy(runId, stored),
            _ => null,
        };

    private static AgentRunAuditActionEntry? ParseAction(
        AgentRunId runId,
        StoredEntry stored)
    {
        if (stored.Events.Count is < 1 or > 4)
        {
            return null;
        }

        var firstEvent = stored.Events[0].Event;
        if (!string.Equals(
                firstEvent.CorrelationId,
                stored.EntryKey,
                StringComparison.Ordinal)
            || firstEvent.Target is not
            {
                Kind: AgentTargetKind,
                Id: var targetFingerprintText,
            })
        {
            return null;
        }

        AgentActionDigest targetFingerprint;
        try
        {
            targetFingerprint = new AgentActionDigest(targetFingerprintText);
        }
        catch (ArgumentException)
        {
            return null;
        }

        AuditDetails.AgentActionDetails? firstDetails = null;
        var phases = new List<AgentRunAuditPhase>(stored.Events.Count);
        AgentAuthorizationSource? authorizationSource = null;
        AgentAuthorizationErrorCode? errorCode = null;
        string? resultCode = null;
        long? policyGeneration = null;
        long? executionDuration = null;
        int? resultCount = null;
        AgentActionDigest? targetIdentity = null;
        foreach (var storedEvent in stored.Events)
        {
            var auditEvent = storedEvent.Event;
            if (auditEvent.Details is not
                    AuditDetails.AgentActionDetails details
                || details.RunId != runId
                || !string.Equals(
                    auditEvent.CorrelationId,
                    stored.EntryKey,
                    StringComparison.Ordinal)
                || !string.Equals(
                    auditEvent.Action,
                    firstEvent.Action,
                    StringComparison.Ordinal)
                || auditEvent.Target != firstEvent.Target
                || !MergeExact(
                    ref targetIdentity,
                    details.Binding.TargetIdentity))
            {
                return null;
            }

            firstDetails ??= details;
            if (!MatchesActionIdentity(firstDetails, details)
                || !MergeExact(
                    ref policyGeneration,
                    details.Binding.PolicyGeneration)
                || !MergeExact(
                    ref executionDuration,
                    details.Binding.ExecutionDurationMilliseconds)
                || !MergeExact(ref resultCount, details.Binding.ResultCount)
                || !MergeExact(
                    ref authorizationSource,
                    details.AuthorizationSource)
                || !MergeExact(ref errorCode, details.ErrorCode)
                || !MergeExact(ref resultCode, details.ResultCode)
                || auditEvent.OccurredAt.Offset != TimeSpan.Zero)
            {
                return null;
            }

            try
            {
                phases.Add(new AgentRunAuditPhase(
                    auditEvent.Outcome,
                    auditEvent.Actor.Kind,
                    auditEvent.OccurredAt));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        try
        {
            // Schema-v1 action details predate the explicit target-identity
            // binding. Their live target fingerprint remains the only durable,
            // secret-free target evidence available for presentation.
            var presentedTarget = targetIdentity ?? targetFingerprint;
            return new AgentRunAuditActionEntry(
                DigestEntryId(stored.EntryKey),
                firstEvent.Action,
                firstDetails!.Capability,
                firstDetails.Risk,
                firstDetails.Permission,
                firstDetails.Decision,
                authorizationSource,
                errorCode,
                resultCode,
                policyGeneration,
                presentedTarget,
                executionDuration,
                resultCount,
                phases);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static AgentRunAuditPolicyEntry? ParsePolicy(
        AgentRunId runId,
        StoredEntry stored)
    {
        if (stored.Events.Count != 1)
        {
            return null;
        }

        var auditEvent = stored.Events[0].Event;
        if (auditEvent.Details is not
                AuditDetails.AgentRunPolicyTransitionDetails details
            || details.RunId != runId
            || !string.Equals(
                auditEvent.EventId,
                stored.EntryKey,
                StringComparison.Ordinal)
            || !string.Equals(
                auditEvent.CorrelationId,
                runId.Value,
                StringComparison.Ordinal)
            || !string.Equals(auditEvent.Action, PolicyAction, StringComparison.Ordinal)
            || auditEvent.Outcome != AuditOutcome.Succeeded
            || auditEvent.Actor.Kind != ActorKind.Human
            || auditEvent.Target is not
            {
                Kind: AgentTargetKind,
                Id: var targetIdentity,
            }
            || !string.Equals(
                targetIdentity,
                details.TargetIdentityDigest.Value,
                StringComparison.Ordinal)
            || auditEvent.OccurredAt.Offset != TimeSpan.Zero)
        {
            return null;
        }

        try
        {
            return new AgentRunAuditPolicyEntry(
                DigestEntryId(stored.EntryKey),
                details.Transition,
                details.PolicyGeneration,
                details.TargetIdentityDigest,
                details.YoloExpiresAtUtc,
                auditEvent.OccurredAt);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool MatchesActionIdentity(
        AuditDetails.AgentActionDetails expected,
        AuditDetails.AgentActionDetails candidate) =>
        expected.RunId == candidate.RunId
        && expected.Capability == candidate.Capability
        && expected.Risk == candidate.Risk
        && expected.Permission == candidate.Permission
        && expected.Decision == candidate.Decision
        && expected.ArgumentDigest == candidate.ArgumentDigest;

    private static bool MergeExact<T>(ref T? current, T? candidate)
        where T : struct
    {
        if (candidate is null)
        {
            return true;
        }

        if (current is null)
        {
            current = candidate;
            return true;
        }

        return EqualityComparer<T>.Default.Equals(
            current.Value,
            candidate.Value);
    }

    private static bool MergeExact(ref string? current, string? candidate)
    {
        if (candidate is null)
        {
            return true;
        }

        if (current is null)
        {
            current = candidate;
            return true;
        }

        return string.Equals(current, candidate, StringComparison.Ordinal);
    }

    private static AgentActionDigest DigestEntryId(string entryKey) =>
        AgentActionDigest.FromUtf8(
            $"asura-agent-run-audit-entry-v1\0{entryKey}");

    private static AgentRunAuditCursor EncodeCursor(
        AgentRunId runId,
        long snapshotSequence,
        long beforeSequence)
    {
        var payload = string.Join(
            ':',
            CursorVersion,
            snapshotSequence.ToString(CultureInfo.InvariantCulture),
            beforeSequence.ToString(CultureInfo.InvariantCulture),
            RunDigest(runId));
        return new AgentRunAuditCursor(ToBase64Url(Encoding.UTF8.GetBytes(payload)));
    }

    private static bool TryDecodeCursor(
        AgentRunAuditQuery query,
        out CursorPosition? cursor)
    {
        cursor = null;
        if (query.Before is null)
        {
            return true;
        }

        try
        {
            var payload = Encoding.UTF8.GetString(
                FromBase64Url(query.Before.Value));
            var parts = payload.Split(':');
            if (parts is not
                [
                    CursorVersion,
                    var snapshotSequenceText,
                    var beforeSequenceText,
                    var runDigest,
                ]
                || !long.TryParse(
                    snapshotSequenceText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var snapshotSequence)
                || !long.TryParse(
                    beforeSequenceText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var beforeSequence)
                || snapshotSequence < 1
                || beforeSequence < 1
                || beforeSequence > snapshotSequence
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(runDigest),
                    Encoding.ASCII.GetBytes(RunDigest(query.RunId))))
            {
                return false;
            }

            cursor = new CursorPosition(snapshotSequence, beforeSequence);
            return true;
        }
        catch (Exception exception)
            when (exception is FormatException
                or DecoderFallbackException
                or ArgumentException)
        {
            return false;
        }
    }

    private static string RunDigest(AgentRunId runId) =>
        AgentActionDigest.FromUtf8(
            $"asura-agent-run-audit-cursor-v2\0{runId.Value}").Value;

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value
            .Replace('-', '+')
            .Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Convert.FromBase64String(base64);
    }

    private static AuditStoreResult<AgentRunAuditPage> Failure(
        AuditStoreErrorCode code,
        string message) =>
        AuditStoreResult<AgentRunAuditPage>.Failure(
            new AuditStoreError(code, message));

    private static AuditStoreResult<AgentRunAuditPage> CorruptTrail() =>
        Failure(
            AuditStoreErrorCode.StorageFailure,
            "The agent-run audit trail is invalid.");

    private static AuditStoreErrorCode MapSqliteError(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? AuditStoreErrorCode.StorageUnavailable
            : AuditStoreErrorCode.StorageFailure;

    private static bool IsStorageBoundaryFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException;

    private sealed class StoredEntry(
        string entryKey,
        string kind,
        long latestSequence)
    {
        public string EntryKey { get; } = entryKey;

        public string Kind { get; } = kind;

        public long LatestSequence { get; } = latestSequence;

        public List<StoredEvent> Events { get; } = [];
    }

    private sealed record StoredEvent(long Sequence, AuditEventRecord Event);

    private sealed record StoredPage(
        IReadOnlyList<StoredEntry> Entries,
        long SnapshotSequence);

    private sealed record CursorPosition(
        long SnapshotSequence,
        long BeforeSequence);
}
