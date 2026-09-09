using System.Globalization;
using Asura.Application;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

public sealed class SqliteApplicationRunStore : IApplicationRunStore
{
    private readonly AsuraDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteApplicationRunStore(AsuraDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _timeProvider = timeProvider;
    }

    public async ValueTask<ApplicationRunResult<ApplicationRunStart>> BeginRunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var previous = await ReadStateAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                var runId = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
                var startedAt = _timeProvider.GetUtcNow();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE app_lifecycle
                    SET clean_shutdown = 0,
                        current_run_id = $runId,
                        started_utc = $startedUtc
                    WHERE singleton_id = 1;
                    """;
                command.Parameters.AddWithValue("$runId", runId);
                command.Parameters.AddWithValue(
                    "$startedUtc",
                    startedAt.ToString("O", CultureInfo.InvariantCulture));
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The application lifecycle marker is missing.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ApplicationRunResult<ApplicationRunStart>.Success(
                    new ApplicationRunStart(runId, !previous.WasClean, previous));
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<ApplicationRunStart>(
                ApplicationRunErrorCode.Cancelled,
                "Application startup was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<ApplicationRunStart>(
                MapSqliteError(exception),
                "The application run marker could not be written.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<ApplicationRunStart>(
                MapStorageBoundaryError(exception),
                "The application run marker store is unavailable.");
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> CompleteRunAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE app_lifecycle
                SET clean_shutdown = 1,
                    current_run_id = NULL,
                    started_utc = NULL,
                    last_clean_utc = $lastCleanUtc
                WHERE singleton_id = 1
                    AND clean_shutdown = 0
                    AND current_run_id = $runId;
                """;
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue(
                "$lastCleanUtc",
                _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Failure<Unit>(
                    ApplicationRunErrorCode.RunMismatch,
                    "The application run marker belongs to a different run.");
            }

            return ApplicationRunResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(
                ApplicationRunErrorCode.Cancelled,
                "Application shutdown finalization was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<Unit>(
                MapSqliteError(exception),
                "The application run marker could not be finalized.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<Unit>(
                MapStorageBoundaryError(exception),
                "The application run marker store is unavailable.");
        }
    }

    public async ValueTask<ApplicationRunResult<ApplicationRunState>> GetStateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var state = await ReadStateAsync(connection, null, cancellationToken)
                .ConfigureAwait(false);
            return ApplicationRunResult<ApplicationRunState>.Success(state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<ApplicationRunState>(
                ApplicationRunErrorCode.Cancelled,
                "Reading application recovery state was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<ApplicationRunState>(
                MapSqliteError(exception),
                "The application recovery state could not be read.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<ApplicationRunState>(
                MapStorageBoundaryError(exception),
                "The application run marker store is unavailable.");
        }
    }

    private static async Task<ApplicationRunState> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT clean_shutdown, current_run_id, started_utc, last_clean_utc
            FROM app_lifecycle
            WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The application lifecycle marker is missing.");
        }

        var wasClean = ReadCleanState(reader);
        var runId = ReadOptionalText(reader, 1);
        var startedAt = ReadTimestamp(reader, 2);
        var lastCleanAt = ReadTimestamp(reader, 3);
        if (wasClean
                ? runId is not null || startedAt is not null
                : string.IsNullOrWhiteSpace(runId) || startedAt is null)
        {
            throw new InvalidOperationException(
                "The application lifecycle marker contains an invalid run state.");
        }

        return new ApplicationRunState(runId, wasClean, startedAt, lastCleanAt);
    }

    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, int ordinal)
    {
        var value = ReadOptionalText(reader, ordinal);
        if (value is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var timestamp)
            ? timestamp
            : throw new InvalidOperationException(
                "The application lifecycle marker contains an invalid timestamp.");
    }

    private static bool ReadCleanState(SqliteDataReader reader)
    {
        var value = reader.GetValue(0);
        return value switch
        {
            0L => false,
            1L => true,
            _ => throw new InvalidOperationException(
                "The application lifecycle marker contains an invalid clean state."),
        };
    }

    private static string? ReadOptionalText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) as string
            ?? throw new InvalidOperationException(
                "The application lifecycle marker contains an invalid storage type.");
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
}
