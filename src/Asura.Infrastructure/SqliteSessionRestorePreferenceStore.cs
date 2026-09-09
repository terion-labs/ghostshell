using Asura.Application;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

public sealed class SqliteSessionRestorePreferenceStore : ISessionRestorePreferenceStore
{
    private readonly AsuraDatabase _database;

    public SqliteSessionRestorePreferenceStore(AsuraDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<ApplicationRunResult<bool>> ReadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT restore_sessions_on_start
                FROM session_restore_preference
                WHERE singleton_id = 1;
                """;
            var value = await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            return value switch
            {
                0L => ApplicationRunResult<bool>.Success(false),
                1L => ApplicationRunResult<bool>.Success(true),
                _ => Failure<bool>(
                    ApplicationRunErrorCode.StorageFailure,
                    "The session restore preference contains invalid local data."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<bool>(
                ApplicationRunErrorCode.Cancelled,
                "Loading the session restore preference was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<bool>(
                MapSqliteError(exception),
                "The session restore preference could not be loaded.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<bool>(
                ApplicationRunErrorCode.StorageFailure,
                "Session restore preference storage is unavailable.");
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> WriteAsync(
        bool restoreSessionsOnStart,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE session_restore_preference
                SET restore_sessions_on_start = $restoreSessionsOnStart
                WHERE singleton_id = 1;
                """;
            command.Parameters.AddWithValue(
                "$restoreSessionsOnStart",
                restoreSessionsOnStart ? 1 : 0);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Failure<Unit>(
                    ApplicationRunErrorCode.StorageFailure,
                    "The session restore preference row is missing.");
            }

            return ApplicationRunResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(
                ApplicationRunErrorCode.Cancelled,
                "Saving the session restore preference was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<Unit>(
                MapSqliteError(exception),
                "The session restore preference could not be saved.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<Unit>(
                ApplicationRunErrorCode.StorageFailure,
                "Session restore preference storage is unavailable.");
        }
    }

    private static ApplicationRunResult<T> Failure<T>(
        ApplicationRunErrorCode code,
        string message) =>
        ApplicationRunResult<T>.Failure(new ApplicationRunError(code, message));

    private static ApplicationRunErrorCode MapSqliteError(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? ApplicationRunErrorCode.StorageUnavailable
            : ApplicationRunErrorCode.StorageFailure;

    private static bool IsStorageBoundaryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException;
}
