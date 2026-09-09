using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

public sealed class SqliteAgentModelFavoriteStore : IAgentModelFavoriteStore, IDisposable
{
    private readonly AsuraDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteAgentModelFavoriteStore(
        AsuraDatabase database,
        TimeProvider timeProvider)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event EventHandler? Changed;

    public void Dispose() => _gate.Dispose();

    public async ValueTask<ApplicationRunResult<IReadOnlyList<AgentModelFavorite>>> ListAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT provider_id, model_id
                FROM agent_model_favorites
                ORDER BY created_utc, provider_id, model_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue(
                "$limit",
                AgentModelFavorite.MaximumCount + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var favorites = new List<AgentModelFavorite>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                favorites.Add(new AgentModelFavorite(
                    new AiProviderProfileId(reader.GetString(0)),
                    reader.GetString(1)));
            }

            if (favorites.Count > AgentModelFavorite.MaximumCount)
            {
                return Failure<IReadOnlyList<AgentModelFavorite>>(
                    "Too many favorite AI models are stored in the local profile.");
            }

            return ApplicationRunResult<IReadOnlyList<AgentModelFavorite>>.Success(favorites);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApplicationRunResult<IReadOnlyList<AgentModelFavorite>>.Failure(
                new ApplicationRunError(
                    ApplicationRunErrorCode.Cancelled,
                    "Loading favorite AI models was cancelled."));
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<IReadOnlyList<AgentModelFavorite>>(
                "Favorite AI models could not be loaded.");
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> SetAsync(
        AgentModelFavorite favorite,
        bool isFavorite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(favorite);
        var enteredGate = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (isFavorite && !await CanInsertAsync(
                    connection,
                    favorite,
                    cancellationToken).ConfigureAwait(false))
            {
                return Failure<Unit>(
                    $"At most {AgentModelFavorite.MaximumCount} AI models can be favorited.");
            }

            await using var command = connection.CreateCommand();
            command.CommandText = isFavorite
                ? """
                    INSERT INTO agent_model_favorites(
                        provider_id,
                        model_id,
                        created_utc)
                    VALUES ($providerId, $modelId, $createdUtc)
                    ON CONFLICT(provider_id, model_id) DO NOTHING;
                    """
                : """
                    DELETE FROM agent_model_favorites
                    WHERE provider_id = $providerId AND model_id = $modelId;
                    """;
            command.Parameters.AddWithValue("$providerId", favorite.ProviderId.Value);
            command.Parameters.AddWithValue("$modelId", favorite.ModelId);
            if (isFavorite)
            {
                command.Parameters.AddWithValue(
                    "$createdUtc",
                    _timeProvider.GetUtcNow().ToString("O"));
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApplicationRunResult<Unit>.Failure(new ApplicationRunError(
                ApplicationRunErrorCode.Cancelled,
                "Saving the favorite AI model was cancelled."));
        }
        catch (Exception exception) when (IsStorageBoundaryFailure(exception))
        {
            return Failure<Unit>("The favorite AI model could not be saved.");
        }
        finally
        {
            if (enteredGate)
            {
                _gate.Release();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return ApplicationRunResult<Unit>.Success(Unit.Value);
    }

    private static async ValueTask<bool> CanInsertAsync(
        SqliteConnection connection,
        AgentModelFavorite favorite,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM agent_model_favorites
                WHERE provider_id = $providerId AND model_id = $modelId)
            OR (SELECT COUNT(*) FROM agent_model_favorites) < $maximumCount;
            """;
        command.Parameters.AddWithValue("$providerId", favorite.ProviderId.Value);
        command.Parameters.AddWithValue("$modelId", favorite.ModelId);
        command.Parameters.AddWithValue("$maximumCount", AgentModelFavorite.MaximumCount);
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L) == 1L;
    }

    private static ApplicationRunResult<T> Failure<T>(string message) =>
        ApplicationRunResult<T>.Failure(new ApplicationRunError(
            ApplicationRunErrorCode.StorageFailure,
            message));

    private static bool IsStorageBoundaryFailure(Exception exception) =>
        exception is SqliteException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException;
}
