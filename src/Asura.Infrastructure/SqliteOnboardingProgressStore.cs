using Asura.Application;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

public sealed class SqliteOnboardingProgressStore : IOnboardingProgressStore
{
    private readonly AsuraDatabase _database;

    public SqliteOnboardingProgressStore(AsuraDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<OnboardingProgressResult<OnboardingProgress>> ReadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return OnboardingProgressResult<OnboardingProgress>.Success(
                await ReadAsync(connection, null, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                OnboardingProgressErrorCode.Cancelled,
                "Loading first-run progress was cancelled.");
        }
        catch (InvalidDataException)
        {
            return Failure(
                OnboardingProgressErrorCode.InvalidData,
                "First-run progress contains invalid local data.");
        }
        catch (SqliteException exception)
        {
            return Failure(
                MapSqliteError(exception),
                "First-run progress could not be loaded.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure(
                OnboardingProgressErrorCode.StorageUnavailable,
                "First-run progress storage is unavailable.");
        }
    }

    public async ValueTask<OnboardingProgressResult<OnboardingProgress>> CompleteAsync(
        int version,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                "The completed onboarding version must be positive.");
        }

        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                "The expected onboarding revision must be positive.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var current = await ReadAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                if (current.CompletedVersion >= version)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return OnboardingProgressResult<OnboardingProgress>.Success(current);
                }

                if (current.Revision != expectedRevision)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Failure(
                        OnboardingProgressErrorCode.Conflict,
                        "First-run progress changed in another application instance.");
                }

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE onboarding_progress
                    SET completed_version = $completedVersion,
                        revision = revision + 1
                    WHERE singleton_id = 1
                        AND revision = $expectedRevision;
                    """;
                command.Parameters.AddWithValue("$completedVersion", version);
                command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Failure(
                        OnboardingProgressErrorCode.Conflict,
                        "First-run progress changed before it could be saved.");
                }

                var updated = await ReadAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OnboardingProgressResult<OnboardingProgress>.Success(updated);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                OnboardingProgressErrorCode.Cancelled,
                "Saving first-run progress was cancelled.");
        }
        catch (InvalidDataException)
        {
            return Failure(
                OnboardingProgressErrorCode.InvalidData,
                "First-run progress contains invalid local data.");
        }
        catch (SqliteException exception)
        {
            return Failure(
                MapSqliteError(exception),
                "First-run progress could not be saved.");
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure(
                OnboardingProgressErrorCode.StorageUnavailable,
                "First-run progress storage is unavailable.");
        }
    }

    private static async Task<OnboardingProgress> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT completed_version, revision
            FROM onboarding_progress
            WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The first-run progress row is missing.");
        }

        var completedVersion = ReadInt32(reader, 0);
        var revision = ReadInt64(reader, 1);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("First-run progress contains duplicate rows.");
        }

        try
        {
            return new OnboardingProgress(completedVersion, revision);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                "First-run progress contains values outside the supported range.",
                exception);
        }
    }

    private static int ReadInt32(SqliteDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value is long number && number is >= int.MinValue and <= int.MaxValue
            ? (int)number
            : throw new InvalidDataException(
                "First-run progress contains an invalid integer value.");
    }

    private static long ReadInt64(SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is long value
            ? value
            : throw new InvalidDataException(
                "First-run progress contains an invalid revision value.");

    private static OnboardingProgressResult<OnboardingProgress> Failure(
        OnboardingProgressErrorCode code,
        string message) =>
        OnboardingProgressResult<OnboardingProgress>.Failure(
            new OnboardingProgressError(code, message));

    private static OnboardingProgressErrorCode MapSqliteError(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? OnboardingProgressErrorCode.StorageUnavailable
            : OnboardingProgressErrorCode.StorageFailure;

    private static bool IsStorageBoundaryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException;
}
