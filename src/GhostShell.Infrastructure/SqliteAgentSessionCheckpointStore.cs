using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using Microsoft.Data.Sqlite;

namespace GhostShell.Infrastructure;

/// <summary>
/// Stores one migration-ready, integrity-checked idle checkpoint per native
/// agent run. Every replacement is revision-fenced in an immediate SQLite
/// transaction, so a stale writer cannot roll durable state backward.
/// </summary>
public sealed partial class SqliteAgentSessionCheckpointStore : IAgentSessionCheckpointStore
{
    public const int MaximumListedCheckpoints = 256;

    private readonly GhostShellDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteAgentSessionCheckpointStore(
        GhostShellDatabase database,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<AgentSessionCheckpointStoreResult<Unit>> SaveAsync(
        AgentSessionCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        await SaveCoreAsync(null, checkpoint, cancellationToken).ConfigureAwait(false);

    public async ValueTask<AgentSessionCheckpointStoreResult<Unit>> SaveAsync(
        AgentConversationScopeId conversationScopeId,
        AgentSessionCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        await SaveCoreAsync(conversationScopeId, checkpoint, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<AgentSessionCheckpointStoreResult<Unit>> SaveCoreAsync(
        AgentConversationScopeId? conversationScopeId,
        AgentSessionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var checksum = ComputeChecksum(checkpoint, conversationScopeId?.Value);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var current = await ReadCurrentVersionAsync(
                connection,
                transaction,
                checkpoint.RunId.Value,
                cancellationToken).ConfigureAwait(false);

            if (current is { } version)
            {
                if (!string.Equals(
                    version.WorkspaceId,
                    conversationScopeId?.Value,
                    StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    return Failure<Unit>(
                        AgentSessionCheckpointStoreErrorCode.RevisionConflict,
                        "The agent checkpoint belongs to a different workspace.",
                        version.Revision);
                }

                if (version.Revision > checkpoint.Revision
                    || (version.Revision == checkpoint.Revision
                        && !CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(version.Checksum),
                            Convert.FromHexString(checksum))))
                {
                    await transaction.RollbackAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    return Failure<Unit>(
                        AgentSessionCheckpointStoreErrorCode.RevisionConflict,
                        "The agent checkpoint changed before it could be saved.",
                        version.Revision);
                }

                if (version.Revision == checkpoint.Revision)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return AgentSessionCheckpointStoreResult<Unit>.Success(Unit.Value);
                }
            }

            // Keep insert and update as separate statements. SQLite3MC 3.49.1
            // could return SQLITE_NOMEM for an UPSERT whose DO UPDATE arm had
            // a revision predicate. The immediate transaction and version read
            // above retain the same atomic compare-and-swap fence without that
            // historically unsafe statement shape.
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = current is null
                ? """
                    INSERT INTO agent_session_checkpoints(
                        run_id,
                        workspace_id,
                        schema_version,
                        generation,
                        revision,
                        payload_json,
                        payload_sha256,
                        updated_utc)
                    VALUES (
                        $runId,
                        $workspaceId,
                        $schemaVersion,
                        $generation,
                        $revision,
                        $payloadJson,
                        $payloadSha256,
                        $updatedUtc);
                    """
                : """
                    UPDATE agent_session_checkpoints
                    SET
                        workspace_id = $workspaceId,
                        schema_version = $schemaVersion,
                        generation = $generation,
                        revision = $revision,
                        payload_json = $payloadJson,
                        payload_sha256 = $payloadSha256,
                        updated_utc = $updatedUtc
                    WHERE run_id = $runId
                        AND revision = $expectedRevision
                        AND workspace_id IS $workspaceId;
                    """;
            command.Parameters.AddWithValue("$runId", checkpoint.RunId.Value);
            command.Parameters.AddWithValue(
                "$workspaceId",
                conversationScopeId is { } scope ? scope.Value : DBNull.Value);
            command.Parameters.AddWithValue("$schemaVersion", checkpoint.SchemaVersion);
            command.Parameters.AddWithValue("$generation", checkpoint.Generation);
            command.Parameters.AddWithValue("$revision", checkpoint.Revision);
            command.Parameters.AddWithValue("$payloadJson", checkpoint.PayloadJson);
            command.Parameters.AddWithValue("$payloadSha256", checksum);
            command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(checkpoint.UpdatedAt));
            if (current is not null)
            {
                command.Parameters.AddWithValue("$expectedRevision", current.Revision);
            }
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "The agent checkpoint write lost its revision fence.");
            }

            // Checkpoints remain subject to retention even if the later,
            // separate presentation-metadata write never commits.
            var retention = await ReadRetentionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            await PruneHistoryAsync(connection, transaction, retention,
                    checkpoint.RunId, checkpoint.UpdatedAt, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AgentSessionCheckpointStoreResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(
                AgentSessionCheckpointStoreErrorCode.Cancelled,
                "Saving the agent checkpoint was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<Unit>(
                MapSqliteError(exception),
                "The agent checkpoint could not be saved.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or ArgumentException
                or OverflowException
                or FormatException)
        {
            return Failure<Unit>(
                AgentSessionCheckpointStoreErrorCode.InvalidCheckpoint,
                "The agent checkpoint is invalid and was not saved.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<Unit>(
                MapStorageBoundaryError(exception),
                "The agent checkpoint store is unavailable.");
        }
    }

    public async ValueTask<AgentSessionCheckpointStoreResult<AgentSessionCheckpoint>>
        LoadAsync(AgentRunId runId, CancellationToken cancellationToken) =>
        await LoadCoreAsync(null, runId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<AgentSessionCheckpointStoreResult<AgentSessionCheckpoint>>
        LoadAsync(
            AgentConversationScopeId conversationScopeId,
            AgentRunId runId,
            CancellationToken cancellationToken) =>
        await LoadCoreAsync(conversationScopeId, runId, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<AgentSessionCheckpointStoreResult<AgentSessionCheckpoint>>
        LoadCoreAsync(
            AgentConversationScopeId? conversationScopeId,
            AgentRunId runId,
            CancellationToken cancellationToken)
    {
        if (!IsBoundedRunId(runId))
        {
            return Failure<AgentSessionCheckpoint>(
                AgentSessionCheckpointStoreErrorCode.InvalidCheckpoint,
                "A bounded agent run ID is required.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    run_id,
                    schema_version,
                    generation,
                    revision,
                    payload_json,
                    payload_sha256,
                    updated_utc,
                    length(CAST(payload_json AS BLOB)),
                    workspace_id
                FROM agent_session_checkpoints
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
                return Failure<AgentSessionCheckpoint>(
                    AgentSessionCheckpointStoreErrorCode.NotFound,
                    "The agent checkpoint was not found.");
            }

            var checkpoint = ReadCheckpoint(reader);
            if (!string.Equals(checkpoint.RunId.Value, runId.Value, StringComparison.Ordinal)
                || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The agent checkpoint identity is invalid.");
            }

            return AgentSessionCheckpointStoreResult<AgentSessionCheckpoint>.Success(
                checkpoint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<AgentSessionCheckpoint>(
                AgentSessionCheckpointStoreErrorCode.Cancelled,
                "Loading the agent checkpoint was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<AgentSessionCheckpoint>(
                MapSqliteError(exception),
                "The agent checkpoint could not be loaded.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or ArgumentException
                or OverflowException
                or FormatException)
        {
            return Failure<AgentSessionCheckpoint>(
                AgentSessionCheckpointStoreErrorCode.CorruptData,
                "The stored agent checkpoint is corrupt and cannot be restored safely.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<AgentSessionCheckpoint>(
                MapStorageBoundaryError(exception),
                "The agent checkpoint store is unavailable.");
        }
    }

    public async ValueTask<AgentSessionCheckpointStoreResult<bool>> DeleteAsync(
        AgentRunId runId,
        CancellationToken cancellationToken) =>
        await DeleteCoreAsync(null, runId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<AgentSessionCheckpointStoreResult<bool>> DeleteAsync(
        AgentConversationScopeId conversationScopeId,
        AgentRunId runId,
        CancellationToken cancellationToken) =>
        await DeleteCoreAsync(conversationScopeId, runId, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<AgentSessionCheckpointStoreResult<bool>> DeleteCoreAsync(
        AgentConversationScopeId? conversationScopeId,
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        if (!IsBoundedRunId(runId))
        {
            return Failure<bool>(
                AgentSessionCheckpointStoreErrorCode.InvalidCheckpoint,
                "A bounded agent run ID is required.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM agent_session_checkpoints
                WHERE run_id = $runId
                    AND ($workspaceId IS NULL OR workspace_id = $workspaceId);
                """;
            command.Parameters.AddWithValue("$runId", runId.Value);
            command.Parameters.AddWithValue(
                "$workspaceId",
                conversationScopeId is { } scope ? scope.Value : DBNull.Value);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (deleted is not 0 and not 1)
            {
                throw new InvalidDataException(
                    "The agent checkpoint inventory is invalid.");
            }

            if (deleted == 1)
            {
                await DeleteRunDataAsync(
                        connection,
                        transaction,
                        runId.Value,
                        createTombstone: true,
                        workspaceId: conversationScopeId?.Value,
                        now: _timeProvider.GetUtcNow().ToUniversalTime(),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AgentSessionCheckpointStoreResult<bool>.Success(deleted == 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<bool>(
                AgentSessionCheckpointStoreErrorCode.Cancelled,
                "Deleting the agent checkpoint was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<bool>(
                MapSqliteError(exception),
                "The agent checkpoint could not be deleted.");
        }
        catch (InvalidDataException)
        {
            return Failure<bool>(
                AgentSessionCheckpointStoreErrorCode.CorruptData,
                "The stored agent checkpoint inventory is corrupt.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<bool>(
                MapStorageBoundaryError(exception),
                "The agent checkpoint store is unavailable.");
        }
    }

    public async ValueTask<AgentSessionCheckpointStoreResult<
        IReadOnlyList<AgentSessionCheckpointSummary>>> ListAsync(
        int maximumCount,
        CancellationToken cancellationToken) =>
        await ListCoreAsync(null, maximumCount, cancellationToken).ConfigureAwait(false);

    public async ValueTask<AgentSessionCheckpointStoreResult<
        IReadOnlyList<AgentSessionCheckpointSummary>>> ListAsync(
        AgentConversationScopeId conversationScopeId,
        int maximumCount,
        CancellationToken cancellationToken) =>
        await ListCoreAsync(conversationScopeId, maximumCount, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<AgentSessionCheckpointStoreResult<
        IReadOnlyList<AgentSessionCheckpointSummary>>> ListCoreAsync(
        AgentConversationScopeId? conversationScopeId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is <= 0 or > MaximumListedCheckpoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                maximumCount,
                $"The checkpoint list size must be between 1 and {MaximumListedCheckpoints}.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    run_id,
                    schema_version,
                    generation,
                    revision,
                    updated_utc,
                    length(CAST(payload_json AS BLOB)),
                    payload_sha256
                FROM agent_session_checkpoints
                WHERE $workspaceId IS NULL OR workspace_id = $workspaceId
                ORDER BY updated_utc DESC, run_id
                LIMIT $maximumCount;
                """;
            command.Parameters.AddWithValue("$maximumCount", maximumCount);
            command.Parameters.AddWithValue(
                "$workspaceId",
                conversationScopeId is { } scope ? scope.Value : DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var summaries = new List<AgentSessionCheckpointSummary>(maximumCount);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var runId = new AgentRunId(ReadBoundedString(
                    reader,
                    0,
                    AgentSessionCheckpoint.MaximumRunIdBytes));
                var schemaVersion = ReadPositiveInt32(reader, 1);
                var generation = ReadNonNegativeInt64(reader, 2);
                var revision = ReadNonNegativeInt64(reader, 3);
                var updatedAt = ReadTimestamp(reader, 4);
                var payloadBytes = ReadNonNegativeInt64(reader, 5);
                _ = ReadChecksum(reader, 6);
                if (payloadBytes is 0 or > AgentSessionCheckpoint.MaximumPayloadBytes)
                {
                    throw new InvalidDataException(
                        "The stored agent checkpoint exceeds its byte limit.");
                }

                summaries.Add(new AgentSessionCheckpointSummary(
                    runId,
                    schemaVersion,
                    generation,
                    revision,
                    updatedAt));
            }

            return AgentSessionCheckpointStoreResult<
                IReadOnlyList<AgentSessionCheckpointSummary>>.Success(summaries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<IReadOnlyList<AgentSessionCheckpointSummary>>(
                AgentSessionCheckpointStoreErrorCode.Cancelled,
                "Listing agent checkpoints was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<IReadOnlyList<AgentSessionCheckpointSummary>>(
                MapSqliteError(exception),
                "Agent checkpoints could not be listed.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or ArgumentException
                or OverflowException
                or FormatException)
        {
            return Failure<IReadOnlyList<AgentSessionCheckpointSummary>>(
                AgentSessionCheckpointStoreErrorCode.CorruptData,
                "The stored agent checkpoint inventory is corrupt.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<IReadOnlyList<AgentSessionCheckpointSummary>>(
                MapStorageBoundaryError(exception),
                "The agent checkpoint store is unavailable.");
        }
    }

    private static async Task<StoredVersion?> ReadCurrentVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, payload_sha256, workspace_id
            FROM agent_session_checkpoints
            WHERE run_id = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var version = new StoredVersion(
            ReadNonNegativeInt64(reader, 0),
            ReadChecksum(reader, 1),
            reader.IsDBNull(2)
                ? null
                : ReadBoundedString(
                    reader,
                    2,
                    AgentConversationScopeId.MaximumUtf8Bytes));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "The agent checkpoint store contains duplicate run identities.");
        }

        return version;
    }

    private static AgentSessionCheckpoint ReadCheckpoint(SqliteDataReader reader)
    {
        var runId = new AgentRunId(ReadBoundedString(
            reader,
            0,
            AgentSessionCheckpoint.MaximumRunIdBytes));
        var schemaVersion = ReadPositiveInt32(reader, 1);
        var generation = ReadNonNegativeInt64(reader, 2);
        var revision = ReadNonNegativeInt64(reader, 3);
        var payloadBytes = ReadNonNegativeInt64(reader, 7);
        if (payloadBytes is 0 or > AgentSessionCheckpoint.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored agent checkpoint exceeds its byte limit.");
        }

        var payload = reader.GetValue(4) as string
            ?? throw new InvalidDataException(
                "The agent checkpoint payload storage is invalid.");
        var storedChecksum = ReadChecksum(reader, 5);
        var updatedAt = ReadTimestamp(reader, 6);
        if (payloadBytes != Encoding.UTF8.GetByteCount(payload))
        {
            throw new InvalidDataException(
                "The stored agent checkpoint exceeds its byte limit.");
        }

        var checkpoint = new AgentSessionCheckpoint(
            runId,
            schemaVersion,
            generation,
            revision,
            payload,
            updatedAt);
        var workspaceId = reader.IsDBNull(8)
            ? null
            : ReadBoundedString(
                reader,
                8,
                AgentConversationScopeId.MaximumUtf8Bytes);
        var expectedChecksum = ComputeChecksum(checkpoint, workspaceId);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(storedChecksum),
                Convert.FromHexString(expectedChecksum)))
        {
            throw new InvalidDataException(
                "The agent checkpoint integrity checksum does not match.");
        }

        return checkpoint;
    }

    private static string ComputeChecksum(
        AgentSessionCheckpoint checkpoint,
        string? workspaceId = null)
    {
        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"{checkpoint.RunId.Value}\n{checkpoint.SchemaVersion}\n{checkpoint.Generation}\n{checkpoint.Revision}\n{FormatTimestamp(checkpoint.UpdatedAt)}\n");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(prefix));
        if (workspaceId is not null)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(workspaceId));
            hash.AppendData("\n"u8);
        }
        hash.AppendData(Encoding.UTF8.GetBytes(checkpoint.PayloadJson));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string ReadBoundedString(
        SqliteDataReader reader,
        int ordinal,
        int maximumUtf8Bytes)
    {
        if (reader.GetValue(ordinal) is not string value
            || string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new InvalidDataException(
                "Stored agent checkpoint text is invalid.");
        }

        return value;
    }

    private static string ReadChecksum(SqliteDataReader reader, int ordinal)
    {
        var checksum = ReadBoundedString(reader, ordinal, 64);
        if (checksum.Length != 64
            || checksum.Any(character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "The stored agent checkpoint checksum is invalid.");
        }

        return checksum;
    }

    private static int ReadPositiveInt32(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not long value
            || value is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException(
                "Stored agent checkpoint version metadata is invalid.");
        }

        return checked((int)value);
    }

    private static long ReadNonNegativeInt64(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not long value || value < 0)
        {
            throw new InvalidDataException(
                "Stored agent checkpoint sequence metadata is invalid.");
        }

        return value;
    }

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not string text
            || !DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value)
            || value.Offset != TimeSpan.Zero
            || !string.Equals(text, FormatTimestamp(value), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored agent checkpoint timestamp is invalid.");
        }

        return value;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool IsBoundedRunId(AgentRunId runId) =>
        runId != default
        && !string.IsNullOrWhiteSpace(runId.Value)
        && Encoding.UTF8.GetByteCount(runId.Value)
            <= AgentSessionCheckpoint.MaximumRunIdBytes
        && !runId.Value.Any(character =>
            char.IsControl(character) || char.IsWhiteSpace(character));

    private static AgentSessionCheckpointStoreResult<T> Failure<T>(
        AgentSessionCheckpointStoreErrorCode code,
        string message,
        long? currentRevision = null) =>
        AgentSessionCheckpointStoreResult<T>.Failure(
            new AgentSessionCheckpointStoreError(code, message, currentRevision));

    private static AgentSessionCheckpointStoreErrorCode MapSqliteError(
        SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? AgentSessionCheckpointStoreErrorCode.StorageUnavailable
            : AgentSessionCheckpointStoreErrorCode.StorageFailure;

    private static AgentSessionCheckpointStoreErrorCode MapStorageBoundaryError(
        Exception exception) =>
        exception is IOException or UnauthorizedAccessException
            ? AgentSessionCheckpointStoreErrorCode.StorageUnavailable
            : AgentSessionCheckpointStoreErrorCode.StorageFailure;

    private static bool IsStorageBoundaryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException;

    private sealed record StoredVersion(
        long Revision,
        string Checksum,
        string? WorkspaceId);
}
