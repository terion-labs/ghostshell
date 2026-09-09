using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

public sealed class SqliteAgentPolicyPreferenceStore : IAgentPolicyPreferenceStore
{
    private readonly AsuraDatabase _database;

    public SqliteAgentPolicyPreferenceStore(AsuraDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<ApplicationRunResult<AgentPolicy?>> ReadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT policy_json
                FROM agent_policy_preference
                WHERE singleton_id = 1;
                """;
            var value = await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (value is DBNull or null)
            {
                return ApplicationRunResult<AgentPolicy?>.Success(null);
            }

            var policy = StoredAgentPolicyJson.Read((string)value);
            return policy?.IsValidForDurableStorage() == true
                ? ApplicationRunResult<AgentPolicy?>.Success(policy)
                : Failure<AgentPolicy?>(
                    "The stored default agent policy is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<AgentPolicy?>(
                "Loading the default agent policy was cancelled.",
                ApplicationRunErrorCode.Cancelled);
        }
        catch (JsonException)
        {
            return Failure<AgentPolicy?>(
                "The stored default agent policy is unreadable.");
        }
        catch (SqliteException exception)
        {
            return Failure<AgentPolicy?>(
                "The default agent policy could not be loaded.",
                MapSqliteError(exception));
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> WriteAsync(
        AgentPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsValidForDurableStorage())
        {
            throw new ArgumentException(
                "The default agent policy must be valid for durable storage.",
                nameof(policy));
        }

        try
        {
            var payload = DefinitionJson.SerializeAgentPolicy(policy);
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_policy_preference
                SET policy_json = $policyJson
                WHERE singleton_id = 1;
                """;
            command.Parameters.AddWithValue("$policyJson", payload);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
                ? ApplicationRunResult<Unit>.Success(Unit.Value)
                : Failure<Unit>("The default agent policy row is missing.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(
                "Saving the default agent policy was cancelled.",
                ApplicationRunErrorCode.Cancelled);
        }
        catch (SqliteException exception)
        {
            return Failure<Unit>(
                "The default agent policy could not be saved.",
                MapSqliteError(exception));
        }
    }

    private static ApplicationRunResult<T> Failure<T>(
        string message,
        ApplicationRunErrorCode code = ApplicationRunErrorCode.StorageFailure) =>
        ApplicationRunResult<T>.Failure(new ApplicationRunError(code, message));

    private static ApplicationRunErrorCode MapSqliteError(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? ApplicationRunErrorCode.StorageUnavailable
            : ApplicationRunErrorCode.StorageFailure;
}
