using System.Globalization;
using Asura.Application;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

public sealed class SqliteRuntimeRecoveryStore :
    IRuntimeRecoveryStore,
    IRuntimeRecoveryDataControl
{
    private const int MaximumSnapshotPayloadBytes = 2 * 1024 * 1024;
    private const int MaximumLoadedPayloadBytesPerRun = 16 * 1024 * 1024;
    private const int MaximumCandidateRows =
        (RuntimeRecoveryInventory.MaximumListedRuns + 1)
        * RuntimeRecoveryInventory.MaximumSnapshotsPerRun;

    private readonly AsuraDatabase _database;
    private readonly ApplicationStartupState? _startupState;

    public SqliteRuntimeRecoveryStore(
        AsuraDatabase database,
        ApplicationStartupState startupState)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(startupState);
        _database = database;
        _startupState = startupState;
    }

    internal SqliteRuntimeRecoveryStore(AsuraDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> LoadAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        if (!IsBoundedIdentifier(runId))
        {
            return Failure<IReadOnlyList<RuntimeRecoverySnapshot>>(
                ApplicationRunErrorCode.StorageFailure,
                "A valid run identifier is required to load runtime recovery state.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    run_id,
                    snapshot_key,
                    schema_version,
                    payload_json,
                    updated_utc,
                    length(CAST(payload_json AS BLOB))
                FROM runtime_snapshots
                WHERE run_id = $runId
                ORDER BY snapshot_key
                LIMIT $maximumSnapshots;
                """;
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue(
                "$maximumSnapshots",
                RuntimeRecoveryInventory.MaximumSnapshotsPerRun + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var snapshots = new List<RuntimeRecoverySnapshot>();
            long loadedPayloadBytes = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (snapshots.Count == RuntimeRecoveryInventory.MaximumSnapshotsPerRun)
                {
                    throw new InvalidDataException(
                        "Runtime recovery state exceeds the per-run snapshot limit.");
                }

                var storedRunId = ReadBoundedString(reader, 0, "run identifier", 256);
                if (!string.Equals(storedRunId, runId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Runtime recovery state belongs to an unexpected run.");
                }

                var key = ReadBoundedString(reader, 1, "snapshot key", 256);
                var schemaVersion = ReadPositiveInt32(reader, 2, "schema version");
                var payloadBytes = ReadNonNegativeInt64(reader, 5, "payload size");
                if (payloadBytes > MaximumSnapshotPayloadBytes
                    || checked(loadedPayloadBytes + payloadBytes)
                        > MaximumLoadedPayloadBytesPerRun)
                {
                    throw new InvalidDataException(
                        "Runtime recovery payloads exceed the safe restore limit.");
                }

                var payloadJson = reader.GetValue(3) as string
                    ?? throw new InvalidDataException(
                        "Runtime recovery payload storage is invalid.");
                if (!PortablePayloadSafety.TryValidate(payloadJson, out _))
                {
                    throw new InvalidDataException(
                        "Runtime recovery payload is invalid.");
                }

                loadedPayloadBytes += payloadBytes;
                snapshots.Add(new RuntimeRecoverySnapshot(
                    storedRunId,
                    key,
                    schemaVersion,
                    payloadJson,
                    ReadTimestamp(reader, 4)));
            }

            return ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>.Success(snapshots);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<IReadOnlyList<RuntimeRecoverySnapshot>>(
                ApplicationRunErrorCode.Cancelled,
                "Loading runtime recovery state was cancelled.");
        }
        catch (Exception exception) when (exception is InvalidDataException
            or ArgumentException
            or OverflowException)
        {
            return Failure<IReadOnlyList<RuntimeRecoverySnapshot>>(
                ApplicationRunErrorCode.StorageFailure,
                "Stored runtime recovery state is invalid and cannot be restored safely.");
        }
        catch (SqliteException exception)
        {
            return Failure<IReadOnlyList<RuntimeRecoverySnapshot>>(
                MapSqliteError(exception),
                "Runtime recovery state could not be loaded.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<IReadOnlyList<RuntimeRecoverySnapshot>>(
                MapStorageBoundaryError(exception),
                "The runtime recovery store is unavailable.");
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> SaveAsync(
        RuntimeRecoverySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsBoundedIdentifier(snapshot.RunId)
            || !IsBoundedIdentifier(snapshot.Key)
            || snapshot.SchemaVersion <= 0
            || snapshot.UpdatedAt.Offset != TimeSpan.Zero)
        {
            return Failure<Unit>(
                ApplicationRunErrorCode.StorageFailure,
                "The runtime recovery snapshot has invalid metadata.");
        }

        if (!PortablePayloadSafety.TryValidate(snapshot.PayloadJson, out var error))
        {
            return Failure<Unit>(ApplicationRunErrorCode.StorageFailure, error!);
        }

        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(snapshot.PayloadJson);
        if (payloadBytes > MaximumSnapshotPayloadBytes)
        {
            return Failure<Unit>(
                ApplicationRunErrorCode.StorageFailure,
                "The runtime recovery payload exceeds the safe snapshot limit.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var activeRunId = await ReadActiveRunIdAsync(
                connection,
                transaction,
                ExpectedActiveRunId(requireInitializedState: false),
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(snapshot.RunId, activeRunId, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<Unit>(
                    ApplicationRunErrorCode.RunMismatch,
                    "The runtime recovery snapshot does not belong to the active run.");
            }

            // Keep the bounded-count check and UPSERT as separate statements.
            // SQLite3MC 3.49.1 returned SQLITE_NOMEM for the combined
            // INSERT ... SELECT ... WHERE ... ON CONFLICT form. The immediate
            // transaction keeps this two-statement form atomic.
            await using (var inventory = connection.CreateCommand())
            {
                inventory.Transaction = transaction;
                inventory.CommandText = """
                    SELECT
                        COUNT(*),
                        EXISTS (
                            SELECT 1
                            FROM runtime_snapshots
                            WHERE run_id = $runId AND snapshot_key = $key)
                    FROM runtime_snapshots
                    WHERE run_id = $runId;
                    """;
                inventory.Parameters.AddWithValue("$runId", snapshot.RunId);
                inventory.Parameters.AddWithValue("$key", snapshot.Key);
                await using var reader = await inventory.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || reader.GetValue(0) is not long snapshotCount
                    || reader.GetValue(1) is not long existingSnapshot
                    || snapshotCount < 0
                    || existingSnapshot is not 0 and not 1)
                {
                    throw new InvalidDataException(
                        "Runtime recovery snapshot inventory is invalid.");
                }

                var inventoryIsFull = snapshotCount
                    >= RuntimeRecoveryInventory.MaximumSnapshotsPerRun;
                if (snapshotCount > RuntimeRecoveryInventory.MaximumSnapshotsPerRun
                    || (inventoryIsFull && existingSnapshot == 0))
                {
                    return Failure<Unit>(
                        ApplicationRunErrorCode.StorageFailure,
                        "The active run already contains the maximum recovery snapshots.");
                }
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO runtime_snapshots(
                    run_id, snapshot_key, schema_version, payload_json, updated_utc)
                VALUES ($runId, $key, $schemaVersion, $payloadJson, $updatedUtc)
                ON CONFLICT(run_id, snapshot_key) DO UPDATE SET
                    schema_version = excluded.schema_version,
                    payload_json = excluded.payload_json,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$runId", snapshot.RunId);
            command.Parameters.AddWithValue("$key", snapshot.Key);
            command.Parameters.AddWithValue("$schemaVersion", snapshot.SchemaVersion);
            command.Parameters.AddWithValue("$payloadJson", snapshot.PayloadJson);
            command.Parameters.AddWithValue(
                "$updatedUtc",
                snapshot.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<Unit>(
                    ApplicationRunErrorCode.StorageFailure,
                    "The active run already contains the maximum recovery snapshots.");
            }

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return ApplicationRunResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(
                ApplicationRunErrorCode.Cancelled,
                "Saving runtime recovery state was cancelled.");
        }
        catch (SqliteException exception)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "recovery.sqlite-save.failed",
                exception);
            return Failure<Unit>(
                MapSqliteError(exception),
                "Runtime recovery state could not be saved.");
        }
        catch (InvalidDataException)
        {
            return Failure<Unit>(
                ApplicationRunErrorCode.StorageFailure,
                "Application lifecycle state is invalid; recovery state was not saved.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<Unit>(
                MapStorageBoundaryError(exception),
                "The runtime recovery store is unavailable.");
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        if (!IsBoundedIdentifier(runId))
        {
            return Failure<Unit>(
                ApplicationRunErrorCode.StorageFailure,
                "A valid run identifier is required to discard runtime recovery state.");
        }

        var result = await DeleteInactiveAsync(
            deleteAll: false,
            runId,
            requireInitializedState: false,
            "Discarding runtime recovery state was cancelled.",
            "Runtime recovery state could not be discarded.",
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? ApplicationRunResult<Unit>.Success(Unit.Value)
            : ApplicationRunResult<Unit>.Failure(result.Error!);
    }

    public async ValueTask<ApplicationRunResult<RuntimeRecoveryInventory>> ListAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);
            var activeRunId = await ReadActiveRunIdAsync(
                connection,
                transaction,
                ExpectedActiveRunId(requireInitializedState: true),
                cancellationToken).ConfigureAwait(false);
            var selection = await ReadNewestInactiveRunIdsAsync(
                connection,
                transaction,
                activeRunId,
                cancellationToken).ConfigureAwait(false);
            var runs = await ReadRunSummariesAsync(
                connection,
                transaction,
                selection.RunIds,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return ApplicationRunResult<RuntimeRecoveryInventory>.Success(
                new RuntimeRecoveryInventory(runs, selection.HasAdditionalRuns));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<RuntimeRecoveryInventory>(
                ApplicationRunErrorCode.Cancelled,
                "Loading stored recovery metadata was cancelled.");
        }
        catch (Exception exception) when (exception is InvalidDataException
            or ArgumentException
            or OverflowException)
        {
            return Failure<RuntimeRecoveryInventory>(
                ApplicationRunErrorCode.StorageFailure,
                "Stored recovery metadata is invalid and cannot be displayed safely.");
        }
        catch (SqliteException exception)
        {
            return Failure<RuntimeRecoveryInventory>(
                MapSqliteError(exception),
                "Stored recovery metadata could not be loaded.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<RuntimeRecoveryInventory>(
                MapStorageBoundaryError(exception),
                "The recovery metadata store is unavailable.");
        }
    }

    private static async Task<InactiveRunSelection> ReadNewestInactiveRunIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string activeRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, updated_utc
            FROM runtime_snapshots INDEXED BY runtime_snapshots_updated_idx
            WHERE run_id <> $activeRunId
            ORDER BY updated_utc DESC, run_id DESC, snapshot_key DESC
            LIMIT $maximumCandidateRows;
            """;
        command.Parameters.AddWithValue("$activeRunId", activeRunId);
        command.Parameters.AddWithValue("$maximumCandidateRows", MaximumCandidateRows);

        var runIds = new List<string>(RuntimeRecoveryInventory.MaximumListedRuns);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var candidateRows = 0;
        var hasAdditionalRuns = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidateRows++;
            var runId = ReadBoundedString(reader, 0, "run identifier", 256);
            _ = ReadTimestamp(reader, 1);
            if (!observed.Add(runId))
            {
                continue;
            }

            if (runIds.Count == RuntimeRecoveryInventory.MaximumListedRuns)
            {
                hasAdditionalRuns = true;
                break;
            }

            runIds.Add(runId);
        }

        if (!hasAdditionalRuns
            && candidateRows == MaximumCandidateRows
            && runIds.Count <= RuntimeRecoveryInventory.MaximumListedRuns)
        {
            throw new InvalidDataException(
                "A recovery run exceeds the supported snapshot limit.");
        }

        return new InactiveRunSelection(runIds, hasAdditionalRuns);
    }

    private static async Task<IReadOnlyList<RuntimeRecoveryRunSummary>> ReadRunSummariesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> runIds,
        CancellationToken cancellationToken)
    {
        if (runIds.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameterNames = new string[runIds.Count];
        for (var index = 0; index < runIds.Count; index++)
        {
            var parameterName = $"$run{index.ToString(CultureInfo.InvariantCulture)}";
            parameterNames[index] = parameterName;
            command.Parameters.AddWithValue(parameterName, runIds[index]);
        }

        command.CommandText = $"""
            SELECT
                run_id,
                length(CAST(payload_json AS BLOB)),
                updated_utc
            FROM runtime_snapshots
            WHERE run_id IN ({string.Join(", ", parameterNames)})
            ORDER BY run_id, snapshot_key;
            """;
        var summaries = new Dictionary<string, RunSummaryAccumulator>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var runId = ReadBoundedString(reader, 0, "run identifier", 256);
            if (!summaries.TryGetValue(runId, out var accumulator))
            {
                accumulator = new RunSummaryAccumulator();
                summaries.Add(runId, accumulator);
            }

            accumulator.SnapshotCount++;
            if (accumulator.SnapshotCount > RuntimeRecoveryInventory.MaximumSnapshotsPerRun)
            {
                throw new InvalidDataException(
                    "A recovery run exceeds the supported snapshot limit.");
            }

            var payloadBytes = ReadNonNegativeInt64(reader, 1, "payload size");
            if (payloadBytes > MaximumSnapshotPayloadBytes)
            {
                throw new InvalidDataException(
                    "A recovery snapshot exceeds the supported payload limit.");
            }

            accumulator.PayloadBytes = checked(accumulator.PayloadBytes + payloadBytes);
            var updatedAt = ReadTimestamp(reader, 2);
            if (accumulator.LastUpdatedAt is null || updatedAt > accumulator.LastUpdatedAt)
            {
                accumulator.LastUpdatedAt = updatedAt;
            }
        }

        var result = new RuntimeRecoveryRunSummary[runIds.Count];
        for (var index = 0; index < runIds.Count; index++)
        {
            var runId = runIds[index];
            if (!summaries.TryGetValue(runId, out var accumulator)
                || accumulator.LastUpdatedAt is null)
            {
                throw new InvalidDataException(
                    "Recovery metadata changed within one read transaction.");
            }

            result[index] = new RuntimeRecoveryRunSummary(
                runId,
                accumulator.SnapshotCount,
                accumulator.PayloadBytes,
                accumulator.LastUpdatedAt.Value);
        }

        return result;
    }

    public ValueTask<ApplicationRunResult<long>> DiscardRunAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        if (!IsBoundedIdentifier(runId))
        {
            return ValueTask.FromResult(Failure<long>(
                ApplicationRunErrorCode.StorageFailure,
                "A valid recovery run identifier is required."));
        }

        return DeleteInactiveAsync(
            deleteAll: false,
            runId,
            requireInitializedState: true,
            "Discarding stored recovery data was cancelled.",
            "Stored recovery data could not be discarded.",
            cancellationToken);
    }

    public ValueTask<ApplicationRunResult<long>> DiscardAllAsync(
        CancellationToken cancellationToken) =>
        DeleteInactiveAsync(
            deleteAll: true,
            runId: null,
            requireInitializedState: true,
            cancelledMessage: "Discarding stored recovery data was cancelled.",
            failureMessage: "Stored recovery data could not be discarded.",
            cancellationToken);

    private async ValueTask<ApplicationRunResult<long>> DeleteInactiveAsync(
        bool deleteAll,
        string? runId,
        bool requireInitializedState,
        string cancelledMessage,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var activeRunId = await ReadActiveRunIdAsync(
                connection,
                transaction,
                ExpectedActiveRunId(requireInitializedState),
                cancellationToken).ConfigureAwait(false);
            if (!deleteAll && string.Equals(runId, activeRunId, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<long>(
                    ApplicationRunErrorCode.RunMismatch,
                    "The active run's crash recovery data cannot be discarded.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM runtime_snapshots
                WHERE ($deleteAll = 1 OR run_id = $runId)
                    AND run_id <> $activeRunId;
                """;
            command.Parameters.AddWithValue("$deleteAll", deleteAll ? 1 : 0);
            command.Parameters.AddWithValue("$runId", (object?)runId ?? DBNull.Value);
            command.Parameters.AddWithValue("$activeRunId", activeRunId);

            var deleted = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return ApplicationRunResult<long>.Success(deleted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<long>(ApplicationRunErrorCode.Cancelled, cancelledMessage);
        }
        catch (SqliteException exception)
        {
            return Failure<long>(MapSqliteError(exception), failureMessage);
        }
        catch (InvalidDataException)
        {
            return Failure<long>(
                ApplicationRunErrorCode.StorageFailure,
                "Application lifecycle state is invalid; recovery data was not changed.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<long>(
                MapStorageBoundaryError(exception),
                "The recovery data store is unavailable.");
        }
    }

    private static async Task<string> ReadActiveRunIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? expectedActiveRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT clean_shutdown, current_run_id, started_utc
            FROM app_lifecycle
            WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetValue(0) is not long cleanShutdown
            || cleanShutdown != 0)
        {
            throw new InvalidDataException(
                "Recovery data controls require one active application run.");
        }

        var activeRunId = ReadBoundedString(reader, 1, "active run identifier", 256);
        _ = ReadTimestamp(reader, 2);
        if (expectedActiveRunId is not null
            && !string.Equals(activeRunId, expectedActiveRunId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Application lifecycle state does not match the active process run.");
        }

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "Application lifecycle state contains duplicate rows.");
        }

        return activeRunId;
    }

    private string? ExpectedActiveRunId(bool requireInitializedState)
    {
        if (_startupState is null)
        {
            if (requireInitializedState)
            {
                throw new InvalidDataException(
                    "Recovery data controls require initialized application state.");
            }

            return null;
        }

        var runId = _startupState.Run?.RunId;
        if (!IsBoundedIdentifier(runId))
        {
            throw new InvalidDataException(
                "The active process run identity is unavailable.");
        }

        return runId;
    }

    private static bool IsBoundedIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);

    private static string ReadBoundedString(
        SqliteDataReader reader,
        int ordinal,
        string field,
        int maximumLength)
    {
        if (reader.GetValue(ordinal) is not string value
            || string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Runtime recovery {field} is invalid.");
        }

        return value;
    }

    private static long ReadNonNegativeInt64(
        SqliteDataReader reader,
        int ordinal,
        string field)
    {
        if (reader.GetValue(ordinal) is not long value || value < 0)
        {
            throw new InvalidDataException(
                $"Runtime recovery {field} is invalid.");
        }

        return value;
    }

    private static int ReadPositiveInt32(
        SqliteDataReader reader,
        int ordinal,
        string field)
    {
        if (reader.GetValue(ordinal) is not long value
            || value is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Runtime recovery {field} is invalid.");
        }

        return checked((int)value);
    }

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal)
    {
        var value = ReadBoundedString(reader, ordinal, "timestamp", 64);
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp)
            || timestamp.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Runtime recovery timestamp is invalid.");
        }

        return timestamp;
    }

    private static ApplicationRunResult<T> Failure<T>(
        ApplicationRunErrorCode code,
        string message) =>
        ApplicationRunResult<T>.Failure(new ApplicationRunError(code, message));

    private static ApplicationRunErrorCode MapSqliteError(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? ApplicationRunErrorCode.StorageUnavailable
            : ApplicationRunErrorCode.StorageFailure;

    private static ApplicationRunErrorCode MapStorageBoundaryError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException
            ? ApplicationRunErrorCode.StorageUnavailable
            : ApplicationRunErrorCode.StorageFailure;

    private static bool IsStorageBoundaryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException;

    private sealed record InactiveRunSelection(
        IReadOnlyList<string> RunIds,
        bool HasAdditionalRuns);

    private sealed class RunSummaryAccumulator
    {
        public long SnapshotCount { get; set; }

        public long PayloadBytes { get; set; }

        public DateTimeOffset? LastUpdatedAt { get; set; }
    }
}
