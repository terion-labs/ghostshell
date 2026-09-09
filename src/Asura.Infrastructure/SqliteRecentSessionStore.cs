using System.Globalization;
using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

/// <summary>
/// Stores only the closed recent-session metadata contract. There is deliberately no
/// JSON or extension payload where terminal content or credentials could be smuggled.
/// </summary>
public sealed class SqliteRecentSessionStore :
    IRecentSessionStore,
    IRecentSessionRetentionStore
{
    private readonly AsuraDatabase _database;
    private readonly RecentSessionRetentionPolicy? _fixedRetention;
    private readonly TimeProvider _timeProvider;

    public SqliteRecentSessionStore(
        AsuraDatabase database,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _timeProvider = timeProvider;
    }

    public SqliteRecentSessionStore(
        AsuraDatabase database,
        TimeProvider timeProvider,
        RecentSessionRetentionPolicy retention)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(retention);
        _database = database;
        _timeProvider = timeProvider;
        _fixedRetention = retention;
    }

    public async ValueTask<RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>>
        GetRetentionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var storedPolicy = await ReadStoredRetentionAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>.Success(
                storedPolicy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<StoredRecentSessionRetentionPolicy>(
                RecentSessionStoreErrorCode.Cancelled,
                "Reading recent-session retention was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<StoredRecentSessionRetentionPolicy>(
                MapSqliteError(exception),
                "The recent-session store could not read retention.");
        }
        catch (RecentSessionDataException exception)
        {
            return Corrupt<StoredRecentSessionRetentionPolicy>(exception.ErrorCode);
        }
        catch (InvalidDataException)
        {
            return Corrupt<StoredRecentSessionRetentionPolicy>();
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<StoredRecentSessionRetentionPolicy>(
                RecentSessionStoreErrorCode.StorageUnavailable,
                "The recent-session store is unavailable.");
        }
    }

    public async ValueTask<RecentSessionStoreResult<RecentSessionRetentionUpdateResult>>
        UpdateRetentionAsync(
            RecentSessionRetentionPolicy policy,
            long expectedRevision,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                "An expected recent-session retention revision must be positive.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var current = await ReadStoredRetentionAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.Revision != expectedRevision)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<RecentSessionRetentionUpdateResult>(
                    RecentSessionStoreErrorCode.Conflict,
                    "The recent-session retention policy changed before it could be saved.");
            }

            var nextRevision = checked(current.Revision + 1);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE recent_session_retention
                    SET revision = $nextRevision,
                        maximum_entries = $maximumEntries,
                        maximum_age_ticks = $maximumAgeTicks
                    WHERE singleton_id = 1 AND revision = $expectedRevision;
                    """;
                command.Parameters.AddWithValue("$nextRevision", nextRevision);
                command.Parameters.AddWithValue("$maximumEntries", policy.MaximumEntries);
                command.Parameters.AddWithValue("$maximumAgeTicks", policy.MaximumAge.Ticks);
                command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The recent-session retention policy changed while it was saved.");
                }
            }

            var prunedSessionCount = await PruneAsync(
                    connection,
                    transaction,
                    policy,
                    cancellationToken)
                .ConfigureAwait(false);
            var storedPolicy = new StoredRecentSessionRetentionPolicy(policy, nextRevision);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Success(
                new RecentSessionRetentionUpdateResult(storedPolicy, prunedSessionCount));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<RecentSessionRetentionUpdateResult>(
                RecentSessionStoreErrorCode.Cancelled,
                "Updating recent-session retention was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<RecentSessionRetentionUpdateResult>(
                MapSqliteError(exception),
                "The recent-session store could not update retention.");
        }
        catch (RecentSessionDataException exception)
        {
            return Corrupt<RecentSessionRetentionUpdateResult>(exception.ErrorCode);
        }
        catch (InvalidDataException)
        {
            return Corrupt<RecentSessionRetentionUpdateResult>();
        }
        catch (OverflowException)
        {
            return Corrupt<RecentSessionRetentionUpdateResult>(
                RecentSessionStoreErrorCode.InvalidRetentionData);
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<RecentSessionRetentionUpdateResult>(
                RecentSessionStoreErrorCode.StorageUnavailable,
                "The recent-session store is unavailable.");
        }
    }

    public async ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
        RecentSessionRecord recentSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recentSession);
        if (recentSession.Outcome != RecentSessionOutcome.Active)
        {
            return Failure<Unit>(
                RecentSessionStoreErrorCode.InvalidRecord,
                "A recent session must be active when it is first recorded.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var retention = await ResolveRetentionAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!retention.IsEnabled)
            {
                await PruneAsync(connection, transaction, retention, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RecentSessionStoreResult<Unit>.Success(Unit.Value);
            }

            var existing = await ReadByIdAsync(
                    connection,
                    transaction,
                    recentSession.SessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null && existing != recentSession)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<Unit>(
                    RecentSessionStoreErrorCode.Conflict,
                    "The session identifier already belongs to different history metadata.");
            }

            if (existing is null)
            {
                await InsertAsync(connection, transaction, recentSession, cancellationToken)
                    .ConfigureAwait(false);
            }

            await PruneAsync(connection, transaction, retention, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RecentSessionStoreResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(
                RecentSessionStoreErrorCode.Cancelled,
                "Recording the recent session was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<Unit>(
                MapSqliteError(exception),
                "The recent-session store could not persist the session.");
        }
        catch (RecentSessionDataException exception)
        {
            return Corrupt<Unit>(exception.ErrorCode);
        }
        catch (InvalidDataException)
        {
            return Corrupt<Unit>();
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<Unit>(
                RecentSessionStoreErrorCode.StorageUnavailable,
                "The recent-session store is unavailable.");
        }
    }

    public async ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
        RecentSessionCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var retention = await ResolveRetentionAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!retention.IsEnabled)
            {
                await PruneAsync(connection, transaction, retention, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RecentSessionStoreResult<Unit>.Success(Unit.Value);
            }

            var existing = await ReadByIdAsync(
                    connection,
                    transaction,
                    completion.SessionId,
                    cancellationToken)
                .ConfigureAwait(false);

            // Clearing history is authoritative. A late lifecycle notification must not
            // recreate an entry or turn a privacy action into an application error.
            if (existing is null)
            {
                await PruneAsync(connection, transaction, retention, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RecentSessionStoreResult<Unit>.Success(Unit.Value);
            }

            if (existing.Outcome != RecentSessionOutcome.Active)
            {
                var isReplay = existing.EndedAt == completion.EndedAt
                    && existing.Outcome == completion.Outcome;
                if (!isReplay)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Failure<Unit>(
                        RecentSessionStoreErrorCode.Conflict,
                        "The recent session already has a different terminal outcome.");
                }

                await PruneAsync(connection, transaction, retention, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RecentSessionStoreResult<Unit>.Success(Unit.Value);
            }

            if (completion.EndedAt < existing.StartedAt)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<Unit>(
                    RecentSessionStoreErrorCode.InvalidRecord,
                    "A recent session cannot end before it starts.");
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE recent_sessions
                    SET ended_utc = $endedUtc,
                        outcome = $outcome
                    WHERE session_id = $sessionId AND outcome = 'Active';
                    """;
                command.Parameters.AddWithValue("$sessionId", completion.SessionId.Value);
                command.Parameters.AddWithValue("$endedUtc", FormatTimestamp(completion.EndedAt));
                command.Parameters.AddWithValue("$outcome", completion.Outcome.ToString());
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The recent session changed while its completion was recorded.");
                }
            }

            await PruneAsync(connection, transaction, retention, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RecentSessionStoreResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(
                RecentSessionStoreErrorCode.Cancelled,
                "Recording the recent-session outcome was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<Unit>(
                MapSqliteError(exception),
                "The recent-session store could not persist the outcome.");
        }
        catch (RecentSessionDataException exception)
        {
            return Corrupt<Unit>(exception.ErrorCode);
        }
        catch (InvalidDataException)
        {
            return Corrupt<Unit>();
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<Unit>(
                RecentSessionStoreErrorCode.StorageUnavailable,
                "The recent-session store is unavailable.");
        }
    }

    public async ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>> ListRecentAsync(
        RecentSessionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var retention = await ResolveRetentionAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            await PruneAsync(connection, transaction, retention, cancellationToken)
                .ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT session_id, definition_kind, definition_id, panel_kind, title,
                       started_utc, ended_utc, outcome
                FROM recent_sessions
                WHERE $definitionKind IS NULL OR definition_kind = $definitionKind
                ORDER BY CASE
                             WHEN ended_utc IS NULL THEN started_utc
                             ELSE ended_utc
                         END DESC,
                         started_utc DESC,
                         session_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue(
                "$definitionKind",
                (object?)query.SourceKind?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", query.Limit);
            var sessions = new List<RecentSessionRecord>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    sessions.Add(ReadRecord(reader));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Success(sessions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<IReadOnlyList<RecentSessionRecord>>(
                RecentSessionStoreErrorCode.Cancelled,
                "Reading recent sessions was cancelled.");
        }
        catch (SqliteException exception)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "history.sqlite-read.failed",
                exception);
            return Failure<IReadOnlyList<RecentSessionRecord>>(
                MapSqliteError(exception),
                "The recent-session store could not read history.");
        }
        catch (RecentSessionDataException exception)
        {
            return Corrupt<IReadOnlyList<RecentSessionRecord>>(exception.ErrorCode);
        }
        catch (InvalidDataException)
        {
            return Corrupt<IReadOnlyList<RecentSessionRecord>>();
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<IReadOnlyList<RecentSessionRecord>>(
                RecentSessionStoreErrorCode.StorageUnavailable,
                "The recent-session store is unavailable.");
        }
    }

    public async ValueTask<RecentSessionStoreResult<int>> MarkActiveSessionsInterruptedAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var retention = await ResolveRetentionAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            var interruptedAt = FormatTimestamp(_timeProvider.GetUtcNow());
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE recent_sessions
                SET ended_utc = CASE
                        WHEN started_utc > $interruptedUtc THEN started_utc
                        ELSE $interruptedUtc
                    END,
                    outcome = 'Interrupted'
                WHERE outcome = 'Active';
                """;
            command.Parameters.AddWithValue("$interruptedUtc", interruptedAt);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await PruneAsync(connection, transaction, retention, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RecentSessionStoreResult<int>.Success(affected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<int>(
                RecentSessionStoreErrorCode.Cancelled,
                "Reconciling interrupted recent sessions was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<int>(
                MapSqliteError(exception),
                "The recent-session store could not reconcile interrupted sessions.");
        }
        catch (RecentSessionDataException exception)
        {
            return Corrupt<int>(exception.ErrorCode);
        }
        catch (InvalidDataException)
        {
            return Corrupt<int>();
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<int>(
                RecentSessionStoreErrorCode.StorageUnavailable,
                "The recent-session store is unavailable.");
        }
    }

    public async ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
        DateTimeOffset through,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM recent_sessions
                WHERE CASE
                          WHEN ended_utc IS NULL THEN started_utc
                          ELSE ended_utc
                      END <= $throughUtc;
                """;
            command.Parameters.AddWithValue("$throughUtc", FormatTimestamp(through));
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return RecentSessionStoreResult<int>.Success(affected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<int>(
                RecentSessionStoreErrorCode.Cancelled,
                "Clearing recent-session history was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<int>(
                MapSqliteError(exception),
                "The recent-session store could not clear history.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<int>(
                RecentSessionStoreErrorCode.StorageUnavailable,
                "The recent-session store is unavailable.");
        }
    }

    public async ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM recent_sessions;";
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return RecentSessionStoreResult<int>.Success(affected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<int>(
                RecentSessionStoreErrorCode.Cancelled,
                "Clearing all recent-session history was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<int>(
                MapSqliteError(exception),
                "The recent-session store could not clear all history.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<int>(
                RecentSessionStoreErrorCode.StorageUnavailable,
                "The recent-session store is unavailable.");
        }
    }

    private static async Task<RecentSessionRecord?> ReadByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session_id, definition_kind, definition_id, panel_kind, title,
                   started_utc, ended_utc, outcome
            FROM recent_sessions
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecentSessionRecord recentSession,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recent_sessions(
                session_id, definition_kind, definition_id, panel_kind, title,
                started_utc, ended_utc, outcome)
            VALUES (
                $sessionId, $definitionKind, $definitionId, $panelKind, $title,
                $startedUtc, NULL, $outcome);
            """;
        command.Parameters.AddWithValue("$sessionId", recentSession.SessionId.Value);
        command.Parameters.AddWithValue(
            "$definitionKind",
            recentSession.SourceDefinition.Kind.Value);
        command.Parameters.AddWithValue("$definitionId", recentSession.SourceDefinition.Value);
        command.Parameters.AddWithValue("$panelKind", recentSession.Kind.ToString());
        command.Parameters.AddWithValue("$title", recentSession.Title);
        command.Parameters.AddWithValue("$startedUtc", FormatTimestamp(recentSession.StartedAt));
        command.Parameters.AddWithValue("$outcome", recentSession.Outcome.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecentSessionRetentionPolicy> ResolveRetentionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        _fixedRetention
        ?? (await ReadStoredRetentionAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false)).Policy;

    private static async Task<StoredRecentSessionRetentionPolicy> ReadStoredRetentionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT singleton_id, revision, maximum_entries, maximum_age_ticks
            FROM recent_session_retention
            ORDER BY singleton_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetInt64(0) != 1)
            {
                throw new RecentSessionRetentionDataException(
                    "The recent-session retention policy is missing.");
            }

            var revision = reader.GetInt64(1);
            var maximumEntries = checked((int)reader.GetInt64(2));
            var maximumAge = TimeSpan.FromTicks(reader.GetInt64(3));
            var storedPolicy = new StoredRecentSessionRetentionPolicy(
                new RecentSessionRetentionPolicy(maximumEntries, maximumAge),
                revision);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new RecentSessionRetentionDataException(
                    "The recent-session retention policy has multiple rows.");
            }

            return storedPolicy;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidCastException
                or InvalidOperationException
                or OverflowException)
        {
            throw new RecentSessionRetentionDataException(
                "The recent-session retention policy is invalid.",
                exception);
        }
    }

    private async Task<int> PruneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecentSessionRetentionPolicy retention,
        CancellationToken cancellationToken)
    {
        if (!retention.IsEnabled)
        {
            await using var clearCommand = connection.CreateCommand();
            clearCommand.Transaction = transaction;
            clearCommand.CommandText = "DELETE FROM recent_sessions;";
            return await clearCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var prunedSessionCount = 0;
        await using (var ageCommand = connection.CreateCommand())
        {
            ageCommand.Transaction = transaction;
            ageCommand.CommandText = """
                DELETE FROM recent_sessions
                WHERE CASE
                          WHEN ended_utc IS NULL THEN started_utc
                          ELSE ended_utc
                      END < $cutoffUtc;
                """;
            ageCommand.Parameters.AddWithValue(
                "$cutoffUtc",
                FormatTimestamp(_timeProvider.GetUtcNow() - retention.MaximumAge));
            prunedSessionCount = await ageCommand.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = """
            DELETE FROM recent_sessions
            WHERE session_id IN (
                SELECT session_id
                FROM recent_sessions
                ORDER BY CASE
                             WHEN ended_utc IS NULL THEN started_utc
                             ELSE ended_utc
                         END DESC,
                         started_utc DESC,
                         session_id
                LIMIT -1 OFFSET $maximumEntries);
            """;
        countCommand.Parameters.AddWithValue("$maximumEntries", retention.MaximumEntries);
        prunedSessionCount += await countCommand.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        return prunedSessionCount;
    }

    private static RecentSessionRecord ReadRecord(SqliteDataReader reader)
    {
        try
        {
            if (!TryParseEnum(reader.GetString(3), out PanelKind panelKind)
                || !TryParseEnum(reader.GetString(7), out RecentSessionOutcome outcome)
                || !TryParseTimestamp(reader.GetString(5), out var startedAt))
            {
                throw new RecentSessionRecordDataException(
                    "Recent-session metadata is invalid.");
            }

            DateTimeOffset? endedAt = null;
            if (!reader.IsDBNull(6))
            {
                if (!TryParseTimestamp(reader.GetString(6), out var parsedEndedAt))
                {
                    throw new RecentSessionRecordDataException(
                        "Recent-session metadata is invalid.");
                }

                endedAt = parsedEndedAt;
            }

            return new RecentSessionRecord(
                new SessionId(reader.GetString(0)),
                new DefinitionKey(
                    new DefinitionKind(reader.GetString(1)),
                    reader.GetString(2)),
                panelKind,
                reader.GetString(4),
                startedAt,
                endedAt,
                outcome);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or InvalidCastException)
        {
            throw new RecentSessionRecordDataException(
                "Recent-session metadata is invalid.",
                exception);
        }
    }

    private static bool TryParseTimestamp(string text, out DateTimeOffset value)
    {
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value))
        {
            return false;
        }

        value = value.ToUniversalTime();
        return string.Equals(text, FormatTimestamp(value), StringComparison.Ordinal);
    }

    private static bool TryParseEnum<T>(string text, out T value)
        where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: false, out value)
        && Enum.IsDefined(value)
        && string.Equals(value.ToString(), text, StringComparison.Ordinal);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static RecentSessionStoreResult<T> Corrupt<T>() =>
        Failure<T>(
            RecentSessionStoreErrorCode.StorageFailure,
            "The recent-session store contains invalid metadata.");

    private static RecentSessionStoreResult<T> Corrupt<T>(
        RecentSessionStoreErrorCode code) =>
        Failure<T>(code, "The recent-session store contains invalid metadata.");

    private static RecentSessionStoreResult<T> Failure<T>(
        RecentSessionStoreErrorCode code,
        string message) =>
        RecentSessionStoreResult<T>.Failure(new RecentSessionStoreError(code, message));

    private static RecentSessionStoreErrorCode MapSqliteError(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? RecentSessionStoreErrorCode.StorageUnavailable
            : RecentSessionStoreErrorCode.StorageFailure;

    private static bool IsStorageBoundaryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException;

    private abstract class RecentSessionDataException : Exception
    {
        protected RecentSessionDataException(
            RecentSessionStoreErrorCode errorCode,
            string message,
            Exception? innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }

        public RecentSessionStoreErrorCode ErrorCode { get; }

    }

    private sealed class RecentSessionRetentionDataException : RecentSessionDataException
    {
        public RecentSessionRetentionDataException(
            string message,
            Exception? innerException = null)
            : base(
                RecentSessionStoreErrorCode.InvalidRetentionData,
                message,
                innerException)
        {
        }

    }

    private sealed class RecentSessionRecordDataException : RecentSessionDataException
    {
        public RecentSessionRecordDataException(
            string message,
            Exception? innerException = null)
            : base(
                RecentSessionStoreErrorCode.InvalidHistoryData,
                message,
                innerException)
        {
        }

    }
}
