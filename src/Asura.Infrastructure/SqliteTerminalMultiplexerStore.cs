using System.Globalization;
using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

public sealed class SqliteTerminalMultiplexerStore :
    ITerminalMultiplexingPreferenceStore,
    ITerminalMultiplexerLeaseStore
{
    private readonly AsuraDatabase _database;

    public SqliteTerminalMultiplexerStore(AsuraDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<ApplicationRunResult<TerminalMultiplexingMode>> ReadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT mode FROM terminal_multiplexing_preference WHERE singleton_id = 1;";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is long raw && Enum.IsDefined((TerminalMultiplexingMode)(int)raw)
                ? ApplicationRunResult<TerminalMultiplexingMode>.Success((TerminalMultiplexingMode)(int)raw)
                : Failure<TerminalMultiplexingMode>("The terminal multiplexing preference is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<TerminalMultiplexingMode>();
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Failure<TerminalMultiplexingMode>("The terminal multiplexing preference could not be loaded.");
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> WriteAsync(
        TerminalMultiplexingMode mode,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE terminal_multiplexing_preference SET mode = $mode WHERE singleton_id = 1;";
            command.Parameters.AddWithValue("$mode", (int)mode);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
                ? ApplicationRunResult<Unit>.Success(Unit.Value)
                : Failure<Unit>("The terminal multiplexing preference row is missing.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<Unit>();
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Failure<Unit>("The terminal multiplexing preference could not be saved.");
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> UpsertAsync(
        TerminalMultiplexerLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO terminal_multiplexer_leases(
                    connection_id, session_name, state, created_utc, updated_utc)
                VALUES ($connectionId, $sessionName, $state, $createdUtc, $updatedUtc)
                ON CONFLICT(connection_id, session_name) DO UPDATE SET
                    state = excluded.state,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$connectionId", lease.ConnectionId.Value);
            command.Parameters.AddWithValue("$sessionName", lease.Session.SessionName);
            command.Parameters.AddWithValue("$state", (int)lease.State);
            command.Parameters.AddWithValue("$createdUtc", Format(lease.CreatedAt));
            command.Parameters.AddWithValue("$updatedUtc", Format(lease.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return ApplicationRunResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<Unit>();
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Failure<Unit>("The managed remote session could not be saved.");
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> DeleteAsync(
        ConnectionId connectionId,
        string sessionName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM terminal_multiplexer_leases
                WHERE connection_id = $connectionId AND session_name = $sessionName;
                """;
            command.Parameters.AddWithValue("$connectionId", connectionId.Value);
            command.Parameters.AddWithValue("$sessionName", sessionName);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return ApplicationRunResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<Unit>();
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Failure<Unit>("The managed remote session could not be removed.");
        }
    }

    public async ValueTask<ApplicationRunResult<IReadOnlyList<TerminalMultiplexerLease>>> ListAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT connection_id, session_name, state, created_utc, updated_utc
                FROM terminal_multiplexer_leases
                ORDER BY updated_utc DESC, connection_id, session_name;
                """;
            var leases = new List<TerminalMultiplexerLease>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                leases.Add(new TerminalMultiplexerLease(
                    new ConnectionId(reader.GetString(0)),
                    new TerminalMultiplexerSession(
                        TerminalMultiplexingMode.Automatic,
                        reader.GetString(1),
                        isEstablished: true),
                    (TerminalMultiplexerLeaseState)reader.GetInt32(2),
                    Parse(reader.GetString(3)),
                    Parse(reader.GetString(4))));
            }

            return ApplicationRunResult<IReadOnlyList<TerminalMultiplexerLease>>.Success(leases.AsReadOnly());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<IReadOnlyList<TerminalMultiplexerLease>>();
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Failure<IReadOnlyList<TerminalMultiplexerLease>>("Managed remote sessions could not be loaded.");
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static bool IsStorageFailure(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException
            or InvalidOperationException or FormatException or ArgumentException;

    private static ApplicationRunResult<T> Cancelled<T>() =>
        ApplicationRunResult<T>.Failure(new ApplicationRunError(
            ApplicationRunErrorCode.Cancelled,
            "The operation was cancelled."));

    private static ApplicationRunResult<T> Failure<T>(string message) =>
        ApplicationRunResult<T>.Failure(new ApplicationRunError(
            ApplicationRunErrorCode.StorageFailure,
            message));
}
