using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;
using Microsoft.Data.Sqlite;

namespace GhostShell.Infrastructure;

public sealed partial class SqliteAgentSessionCheckpointStore
{
    private const int MaximumHistoryExportRuns = 256;

    public async ValueTask<AgentSessionCheckpointStoreResult<Unit>>
        SaveHistoryMetadataAsync(
            AgentConversationScopeId? conversationScopeId,
            AgentRunHistoryMetadata metadata,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.RunId == default
            || metadata.PolicyGeneration < 1
            || metadata.UpdatedAtUtc.Offset != TimeSpan.Zero)
        {
            return HistoryFailure<Unit>(
                AgentSessionCheckpointStoreErrorCode.InvalidCheckpoint,
                "Agent history metadata is invalid.");
        }

        try
        {
            var baselineJson = WritePolicy(metadata.BaselinePolicy);
            var runJson = WritePolicy(metadata.RunPolicy);
            var effectiveJson = WritePolicy(metadata.EffectivePolicy);
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO agent_run_history_metadata(
                        run_id,
                        workspace_id,
                        provider_id,
                        model_id,
                        baseline_policy_json,
                        run_policy_json,
                        effective_policy_json,
                        policy_generation,
                        updated_utc)
                    VALUES (
                        $runId,
                        $workspaceId,
                        $providerId,
                        $modelId,
                        $baselinePolicy,
                        $runPolicy,
                        $effectivePolicy,
                        $policyGeneration,
                        $updatedUtc)
                    ON CONFLICT(run_id) DO UPDATE SET
                        workspace_id = excluded.workspace_id,
                        provider_id = excluded.provider_id,
                        model_id = excluded.model_id,
                        baseline_policy_json = excluded.baseline_policy_json,
                        run_policy_json = excluded.run_policy_json,
                        effective_policy_json = excluded.effective_policy_json,
                        policy_generation = excluded.policy_generation,
                        updated_utc = excluded.updated_utc;

                    DELETE FROM agent_run_history_tombstones
                    WHERE run_id = $runId;
                    """;
                AddHistoryMetadataParameters(
                    command,
                    conversationScopeId,
                    metadata,
                    baselineJson,
                    runJson,
                    effectiveJson);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var retention = await ReadRetentionAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            await PruneHistoryAsync(
                    connection,
                    transaction,
                    retention,
                    metadata.RunId,
                    metadata.UpdatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AgentSessionCheckpointStoreResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HistoryFailure<Unit>(
                AgentSessionCheckpointStoreErrorCode.Cancelled,
                "Saving agent history metadata was cancelled.");
        }
        catch (Exception exception) when (IsHistoryDataFailure(exception))
        {
            return HistoryFailure<Unit>(
                AgentSessionCheckpointStoreErrorCode.CorruptData,
                "Agent history metadata is invalid.");
        }
        catch (SqliteException exception)
        {
            return HistoryFailure<Unit>(
                MapSqliteError(exception),
                "Agent history metadata could not be saved.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return HistoryFailure<Unit>(
                MapStorageBoundaryError(exception),
                "The agent history store is unavailable.");
        }
    }

    public async ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryMetadata>>
        LoadHistoryMetadataAsync(
            AgentConversationScopeId? conversationScopeId,
            AgentRunId runId,
            CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    provider_id,
                    model_id,
                    baseline_policy_json,
                    run_policy_json,
                    effective_policy_json,
                    policy_generation,
                    updated_utc
                FROM agent_run_history_metadata
                WHERE run_id = $runId
                  AND ($workspaceId IS NULL OR workspace_id = $workspaceId);
                """;
            command.Parameters.AddWithValue("$runId", runId.Value);
            command.Parameters.AddWithValue(
                "$workspaceId",
                conversationScopeId is { } scope ? scope.Value : DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return HistoryFailure<AgentRunHistoryMetadata>(
                    AgentSessionCheckpointStoreErrorCode.NotFound,
                    "Agent history metadata was not found.");
            }

            var metadata = ReadMetadata(reader, runId);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "The agent history contains duplicate run identities.");
            }

            return AgentSessionCheckpointStoreResult<AgentRunHistoryMetadata>.Success(
                metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HistoryFailure<AgentRunHistoryMetadata>(
                AgentSessionCheckpointStoreErrorCode.Cancelled,
                "Reading agent history metadata was cancelled.");
        }
        catch (Exception exception) when (IsHistoryDataFailure(exception))
        {
            return HistoryFailure<AgentRunHistoryMetadata>(
                AgentSessionCheckpointStoreErrorCode.CorruptData,
                "Stored agent history metadata is corrupt.");
        }
        catch (SqliteException exception)
        {
            return HistoryFailure<AgentRunHistoryMetadata>(
                MapSqliteError(exception),
                "Agent history metadata could not be read.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return HistoryFailure<AgentRunHistoryMetadata>(
                MapStorageBoundaryError(exception),
                "The agent history store is unavailable.");
        }
    }

    public async ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>>
        GetHistoryRetentionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>.Success(
                await ReadRetentionAsync(connection, null, cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HistoryFailure<AgentRunHistoryRetention>(
                AgentSessionCheckpointStoreErrorCode.Cancelled,
                "Reading agent history retention was cancelled.");
        }
        catch (Exception exception) when (IsHistoryDataFailure(exception))
        {
            return HistoryFailure<AgentRunHistoryRetention>(
                AgentSessionCheckpointStoreErrorCode.CorruptData,
                "Stored agent history retention is corrupt.");
        }
        catch (SqliteException exception)
        {
            return HistoryFailure<AgentRunHistoryRetention>(
                MapSqliteError(exception),
                "Agent history retention could not be read.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return HistoryFailure<AgentRunHistoryRetention>(
                MapStorageBoundaryError(exception),
                "The agent history store is unavailable.");
        }
    }

    public async ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>>
        UpdateHistoryRetentionAsync(
            AgentConversationScopeId? conversationScopeId,
            AgentRunHistoryRetention expected,
            int maximumRuns,
            TimeSpan maximumAge,
            AgentRunId? protectedRunId,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        AgentRunHistoryRetention requested;
        try
        {
            requested = new AgentRunHistoryRetention(
                maximumRuns,
                maximumAge,
                checked(expected.Revision + 1));
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return HistoryFailure<AgentRunHistoryRetention>(
                AgentSessionCheckpointStoreErrorCode.InvalidCheckpoint,
                "The requested agent history retention is invalid.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var current = await ReadRetentionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (current.Revision != expected.Revision
                || current.MaximumRuns != expected.MaximumRuns
                || current.MaximumAge != expected.MaximumAge)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>.Failure(
                    new AgentSessionCheckpointStoreError(
                        AgentSessionCheckpointStoreErrorCode.RevisionConflict,
                        "Agent history retention changed before it could be saved.",
                        current.Revision));
            }

            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE agent_run_history_retention
                    SET
                        revision = $revision,
                        maximum_runs = $maximumRuns,
                        maximum_age_ticks = $maximumAgeTicks,
                        updated_utc = $updatedUtc
                    WHERE singleton_id = 1 AND revision = $expectedRevision;

                    INSERT INTO agent_run_history_retention_events(
                        revision,
                        previous_maximum_runs,
                        maximum_runs,
                        previous_maximum_age_ticks,
                        maximum_age_ticks,
                        occurred_utc)
                    VALUES (
                        $revision,
                        $previousMaximumRuns,
                        $maximumRuns,
                        $previousMaximumAgeTicks,
                        $maximumAgeTicks,
                        $updatedUtc);

                    DELETE FROM agent_run_history_retention_events
                    WHERE revision NOT IN (
                        SELECT revision
                        FROM agent_run_history_retention_events
                        ORDER BY revision DESC
                        LIMIT 256);
                    """;
                command.Parameters.AddWithValue("$revision", requested.Revision);
                command.Parameters.AddWithValue("$expectedRevision", expected.Revision);
                command.Parameters.AddWithValue("$maximumRuns", requested.MaximumRuns);
                command.Parameters.AddWithValue("$maximumAgeTicks", requested.MaximumAge.Ticks);
                command.Parameters.AddWithValue(
                    "$previousMaximumRuns",
                    expected.MaximumRuns);
                command.Parameters.AddWithValue(
                    "$previousMaximumAgeTicks",
                    expected.MaximumAge.Ticks);
                command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(now));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await PruneHistoryAsync(
                    connection,
                    transaction,
                    requested,
                    protectedRunId,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _ = conversationScopeId;
            return AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>.Success(
                requested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HistoryFailure<AgentRunHistoryRetention>(
                AgentSessionCheckpointStoreErrorCode.Cancelled,
                "Updating agent history retention was cancelled.");
        }
        catch (Exception exception) when (IsHistoryDataFailure(exception))
        {
            return HistoryFailure<AgentRunHistoryRetention>(
                AgentSessionCheckpointStoreErrorCode.CorruptData,
                "Stored agent history retention is corrupt.");
        }
        catch (SqliteException exception)
        {
            return HistoryFailure<AgentRunHistoryRetention>(
                MapSqliteError(exception),
                "Agent history retention could not be updated.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return HistoryFailure<AgentRunHistoryRetention>(
                MapStorageBoundaryError(exception),
                "The agent history store is unavailable.");
        }
    }

    public async ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>>
        ExportHistoryAsync(
            AgentConversationScopeId? conversationScopeId,
            Stream destination,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            return HistoryFailure<AgentRunHistoryExportReceipt>(
                AgentSessionCheckpointStoreErrorCode.StorageUnavailable,
                "The agent history export destination is not writable.");
        }

        try
        {
            var metadata = await ReadAllMetadataAsync(
                    conversationScopeId,
                    cancellationToken)
                .ConfigureAwait(false);
            var tombstones = await ReadTombstonesAsync(
                    conversationScopeId,
                    cancellationToken)
                .ConfigureAwait(false);
            var auditReader = new SqliteAgentRunAuditReader(_database);
            var trails = new List<(AgentRunHistoryMetadata Metadata, AgentRunAuditEntry[] Entries)>();
            foreach (var item in metadata)
            {
                var entries = new List<AgentRunAuditEntry>();
                AgentRunAuditCursor? cursor = null;
                do
                {
                    var page = await auditReader.ReadAsync(
                            new AgentRunAuditQuery(
                                item.RunId,
                                cursor,
                                AgentRunAuditQuery.MaximumPageSize),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!page.IsSuccess || page.Value is null)
                    {
                        return HistoryFailure<AgentRunHistoryExportReceipt>(
                            AgentSessionCheckpointStoreErrorCode.CorruptData,
                            "Agent history export stopped because an audit trail is unavailable or corrupt.");
                    }

                    entries.AddRange(page.Value.Entries);
                    cursor = page.Value.Next;
                }
                while (cursor is not null);

                trails.Add((item, [.. entries]));
            }

            var exportedAt = _timeProvider.GetUtcNow().ToUniversalTime();
            var json = WriteExport(trails, tombstones, exportedAt);
            await destination.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            return AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>.Success(
                new AgentRunHistoryExportReceipt(
                    trails.Count,
                    exportedAt,
                    json.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(json))));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HistoryFailure<AgentRunHistoryExportReceipt>(
                AgentSessionCheckpointStoreErrorCode.Cancelled,
                "Exporting agent history was cancelled.");
        }
        catch (Exception exception) when (IsHistoryDataFailure(exception))
        {
            return HistoryFailure<AgentRunHistoryExportReceipt>(
                AgentSessionCheckpointStoreErrorCode.CorruptData,
                "Agent history export stopped because stored metadata is corrupt.");
        }
        catch (SqliteException exception)
        {
            return HistoryFailure<AgentRunHistoryExportReceipt>(
                MapSqliteError(exception),
                "Agent history could not be exported.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return HistoryFailure<AgentRunHistoryExportReceipt>(
                MapStorageBoundaryError(exception),
                "The agent history export destination is unavailable.");
        }
    }

    private static void AddHistoryMetadataParameters(
        SqliteCommand command,
        AgentConversationScopeId? conversationScopeId,
        AgentRunHistoryMetadata metadata,
        string baselineJson,
        string runJson,
        string effectiveJson)
    {
        command.Parameters.AddWithValue("$runId", metadata.RunId.Value);
        command.Parameters.AddWithValue(
            "$workspaceId",
            conversationScopeId is { } scope ? scope.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "$providerId",
            metadata.ProviderId is { } provider ? provider.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "$modelId",
            metadata.ModelId is { } model ? model : DBNull.Value);
        command.Parameters.AddWithValue("$baselinePolicy", baselineJson);
        command.Parameters.AddWithValue("$runPolicy", runJson);
        command.Parameters.AddWithValue("$effectivePolicy", effectiveJson);
        command.Parameters.AddWithValue("$policyGeneration", metadata.PolicyGeneration);
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(metadata.UpdatedAtUtc));
    }

    private static async ValueTask<AgentRunHistoryRetention> ReadRetentionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT maximum_runs, maximum_age_ticks, revision
            FROM agent_run_history_retention
            WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Agent history retention is missing.");
        }

        var retention = new AgentRunHistoryRetention(
            reader.GetInt32(0),
            TimeSpan.FromTicks(reader.GetInt64(1)),
            reader.GetInt64(2));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Agent history retention is duplicated.");
        }

        return retention;
    }

    private static async ValueTask PruneHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentRunHistoryRetention retention,
        AgentRunId? protectedRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = now.ToUniversalTime() - retention.MaximumAge;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH activity AS (
                SELECT run_id, updated_utc FROM agent_run_history_metadata
                UNION ALL
                SELECT run_id, updated_utc FROM agent_session_checkpoints
            ), latest AS (
                SELECT run_id, MAX(updated_utc) AS updated_utc
                FROM activity
                GROUP BY run_id
            ), ranked AS (
                SELECT
                    run_id,
                    updated_utc,
                    ROW_NUMBER() OVER (
                        ORDER BY updated_utc DESC, run_id) AS position
                FROM latest
                WHERE $protectedRunId IS NULL OR run_id <> $protectedRunId
            )
            SELECT run_id
            FROM ranked
            WHERE position > $maximumUnprotectedRuns OR updated_utc < $cutoffUtc
            ORDER BY run_id;
            """;
        command.Parameters.AddWithValue(
            "$maximumUnprotectedRuns",
            retention.MaximumRuns - (protectedRunId is null ? 0 : 1));
        command.Parameters.AddWithValue("$cutoffUtc", FormatTimestamp(cutoff));
        command.Parameters.AddWithValue(
            "$protectedRunId",
            protectedRunId is { } protectedId ? protectedId.Value : DBNull.Value);
        var runIds = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                runIds.Add(reader.GetString(0));
            }
        }

        foreach (var runId in runIds)
        {
            await DeleteRunDataAsync(
                    connection,
                    transaction,
                    runId,
                    createTombstone: false,
                    workspaceId: null,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask DeleteRunDataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        bool createTombstone,
        string? workspaceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM agent_action_audit_state
            WHERE action_id IN (
                SELECT correlation_id
                FROM audit_events
                WHERE json_extract(details_json, '$.runId') = $runId
                  AND json_extract(details_json, '$.kind') = 'agent-action');

            DELETE FROM audit_events
            WHERE json_extract(details_json, '$.runId') = $runId
              AND json_extract(details_json, '$.kind') IN (
                    'agent-action',
                    'agent-run-policy-transition');

            DELETE FROM agent_session_checkpoints WHERE run_id = $runId;
            DELETE FROM agent_run_history_metadata WHERE run_id = $runId;
            DELETE FROM agent_run_history_tombstones WHERE run_id = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (!createTombstone)
        {
            return;
        }

        await using var tombstone = connection.CreateCommand();
        tombstone.Transaction = transaction;
        tombstone.CommandText = """
            INSERT INTO agent_run_history_tombstones(
                run_id,
                workspace_id,
                deleted_utc)
            VALUES ($runId, $workspaceId, $deletedUtc);
            """;
        tombstone.Parameters.AddWithValue("$runId", runId);
        tombstone.Parameters.AddWithValue(
            "$workspaceId",
            workspaceId is null ? DBNull.Value : workspaceId);
        tombstone.Parameters.AddWithValue("$deletedUtc", FormatTimestamp(now));
        await tombstone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var pruneTombstones = connection.CreateCommand();
        pruneTombstones.Transaction = transaction;
        pruneTombstones.CommandText = """
            DELETE FROM agent_run_history_tombstones
            WHERE run_id NOT IN (
                SELECT run_id
                FROM agent_run_history_tombstones
                ORDER BY deleted_utc DESC, run_id
                LIMIT 256);
            """;
        await pruneTombstones.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<AgentRunHistoryMetadata>> ReadAllMetadataAsync(
        AgentConversationScopeId? conversationScopeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                run_id,
                provider_id,
                model_id,
                baseline_policy_json,
                run_policy_json,
                effective_policy_json,
                policy_generation,
                updated_utc
            FROM agent_run_history_metadata
            WHERE $workspaceId IS NULL OR workspace_id = $workspaceId
            ORDER BY updated_utc DESC, run_id
            LIMIT $maximumRuns;
            """;
        command.Parameters.AddWithValue("$maximumRuns", MaximumHistoryExportRuns);
        command.Parameters.AddWithValue(
            "$workspaceId",
            conversationScopeId is { } scope ? scope.Value : DBNull.Value);
        var values = new List<AgentRunHistoryMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(ReadMetadata(reader, new AgentRunId(reader.GetString(0)), 1));
        }

        return values;
    }

    private async ValueTask<IReadOnlyList<(AgentRunId RunId, DateTimeOffset DeletedAt)>>
        ReadTombstonesAsync(
            AgentConversationScopeId? conversationScopeId,
            CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, deleted_utc
            FROM agent_run_history_tombstones
            WHERE $workspaceId IS NULL OR workspace_id = $workspaceId
            ORDER BY deleted_utc DESC, run_id
            LIMIT $maximumRuns;
            """;
        command.Parameters.AddWithValue("$maximumRuns", MaximumHistoryExportRuns);
        command.Parameters.AddWithValue(
            "$workspaceId",
            conversationScopeId is { } scope ? scope.Value : DBNull.Value);
        var values = new List<(AgentRunId, DateTimeOffset)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add((
                new AgentRunId(reader.GetString(0)),
                DateTimeOffset.Parse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)));
        }

        return values;
    }

    private static AgentRunHistoryMetadata ReadMetadata(
        SqliteDataReader reader,
        AgentRunId runId,
        int offset = 0) =>
        new(
            runId,
            reader.IsDBNull(offset)
                ? null
                : new AiProviderProfileId(reader.GetString(offset)),
            reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1),
            ReadPolicy(reader.GetString(offset + 2)),
            ReadPolicy(reader.GetString(offset + 3)),
            ReadPolicy(reader.GetString(offset + 4)),
            reader.GetInt64(offset + 5),
            DateTimeOffset.Parse(
                reader.GetString(offset + 6),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));

    private static string WritePolicy(AgentRunHistoryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!AgentPolicy.IsValidProvider(policy.ProviderId)
            || !AgentPolicy.IsValidModel(policy.ModelId)
            || policy.Permissions.IsDefault
            || policy.Permissions.Length != AgentPolicy.Capabilities.Length
            || policy.Permissions.Any(item =>
                !Enum.IsDefined(item.Capability)
                || !Enum.IsDefined(item.Permission))
            || policy.Permissions.Select(item => item.Capability).Distinct().Count()
                != AgentPolicy.Capabilities.Length)
        {
            throw new InvalidDataException("An agent history policy is invalid.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("providerId", policy.ProviderId);
            writer.WriteString("modelId", policy.ModelId);
            writer.WriteStartObject("permissions");
            foreach (var item in policy.Permissions.OrderBy(item => item.Capability))
            {
                writer.WriteString(item.Capability.ToString(), item.Permission.ToString());
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static AgentRunHistoryPolicy ReadPolicy(string json)
    {
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 3
            || !root.TryGetProperty("providerId", out var provider)
            || provider.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("modelId", out var model)
            || model.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("permissions", out var permissions)
            || permissions.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("An agent history policy is malformed.");
        }

        var values = new Dictionary<AgentCapability, AgentPermission>();
        foreach (var property in permissions.EnumerateObject())
        {
            if (!Enum.TryParse<AgentCapability>(property.Name, out var capability)
                || !Enum.IsDefined(capability)
                || property.Value.ValueKind != JsonValueKind.String
                || !Enum.TryParse<AgentPermission>(property.Value.GetString(), out var permission)
                || !Enum.IsDefined(permission)
                || !values.TryAdd(capability, permission))
            {
                throw new InvalidDataException("An agent history permission is malformed.");
            }
        }

        if (values.Count != AgentPolicy.Capabilities.Length
            || AgentPolicy.Capabilities.Any(capability => !values.ContainsKey(capability)))
        {
            throw new InvalidDataException("An agent history policy is incomplete.");
        }

        return new AgentRunHistoryPolicy(
            provider.GetString()!,
            model.GetString()!,
            [.. AgentPolicy.Capabilities.Select(capability =>
                new AgentHistoryCapabilityPermission(capability, values[capability]))]);
    }

    private static byte[] WriteExport(
        IReadOnlyList<(AgentRunHistoryMetadata Metadata, AgentRunAuditEntry[] Entries)> trails,
        IReadOnlyList<(AgentRunId RunId, DateTimeOffset DeletedAt)> tombstones,
        DateTimeOffset exportedAt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("exportedAtUtc", exportedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteStartArray("runs");
            foreach (var (metadata, entries) in trails)
            {
                writer.WriteStartObject();
                writer.WriteString("runId", metadata.RunId.Value);
                if (metadata.ProviderId is { } providerId)
                {
                    writer.WriteString("providerProfileId", providerId.Value);
                }

                if (metadata.ModelId is { } modelId)
                {
                    writer.WriteString("modelId", modelId);
                }

                writer.WriteNumber("policyGeneration", metadata.PolicyGeneration);
                writer.WriteString("updatedAtUtc", metadata.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                WriteExportPolicy(writer, "baselinePolicy", metadata.BaselinePolicy);
                WriteExportPolicy(writer, "runPolicy", metadata.RunPolicy);
                WriteExportPolicy(writer, "effectivePolicy", metadata.EffectivePolicy);
                writer.WriteStartArray("audit");
                foreach (var entry in entries)
                {
                    WriteAuditEntry(writer, entry);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("deletedRuns");
            foreach (var tombstone in tombstones)
            {
                writer.WriteStartObject();
                writer.WriteString("runId", tombstone.RunId.Value);
                writer.WriteString("deletedAtUtc", tombstone.DeletedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteExportPolicy(
        Utf8JsonWriter writer,
        string propertyName,
        AgentRunHistoryPolicy policy)
    {
        writer.WritePropertyName(propertyName);
        using var document = JsonDocument.Parse(WritePolicy(policy));
        document.RootElement.WriteTo(writer);
    }

    private static void WriteAuditEntry(Utf8JsonWriter writer, AgentRunAuditEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("entryId", entry.EntryId.Value);
        writer.WriteString("occurredAtUtc", entry.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
        switch (entry)
        {
            case AgentRunAuditActionEntry action:
                writer.WriteString("kind", "action");
                writer.WriteString("toolName", action.ToolName);
                writer.WriteString("capability", action.Capability.ToString());
                writer.WriteString("risk", action.Risk.ToString());
                writer.WriteString("permission", action.Permission.ToString());
                writer.WriteString("latestOutcome", action.LatestOutcome.ToString());
                writer.WriteString("targetIdentity", action.TargetIdentity.Value);
                if (action.AuthorizationSource is { } source)
                {
                    writer.WriteString("authorizationSource", source.ToString());
                }

                if (action.ErrorCode is { } errorCode)
                {
                    writer.WriteString("errorCode", errorCode.ToString());
                }

                if (action.ResultCode is { } resultCode)
                {
                    writer.WriteString("resultCode", resultCode);
                }

                if (action.ExecutionDurationMilliseconds is { } duration)
                {
                    writer.WriteNumber("executionDurationMilliseconds", duration);
                }

                if (action.ResultCount is { } count)
                {
                    writer.WriteNumber("resultCount", count);
                }

                writer.WriteStartArray("phases");
                foreach (var phase in action.Phases)
                {
                    writer.WriteStartObject();
                    writer.WriteString("outcome", phase.Outcome.ToString());
                    writer.WriteString("actorKind", phase.ActorKind.ToString());
                    writer.WriteString("occurredAtUtc", phase.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            case AgentRunAuditPolicyEntry policy:
                writer.WriteString("kind", "policy-transition");
                writer.WriteString("transition", policy.Transition.ToString());
                writer.WriteNumber("policyGeneration", policy.PolicyGeneration);
                writer.WriteString("targetIdentity", policy.TargetIdentity.Value);
                if (policy.YoloExpiresAtUtc is { } expiry)
                {
                    writer.WriteString("fullAccessExpiresAtUtc", expiry.ToString("O", CultureInfo.InvariantCulture));
                }

                break;
            default:
                throw new InvalidDataException("An agent audit entry kind is unsupported.");
        }

        writer.WriteEndObject();
    }

    private static bool IsHistoryDataFailure(Exception exception) =>
        exception is InvalidDataException
            or ArgumentException
            or FormatException
            or OverflowException
            or JsonException;

    private static AgentSessionCheckpointStoreResult<T> HistoryFailure<T>(
        AgentSessionCheckpointStoreErrorCode code,
        string message) =>
        AgentSessionCheckpointStoreResult<T>.Failure(
            new AgentSessionCheckpointStoreError(code, message));
}
