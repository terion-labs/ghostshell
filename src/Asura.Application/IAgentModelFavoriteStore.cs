namespace Asura.Application;

/// <summary>
/// Stores this local profile's model shortcuts. Favorites are presentation
/// preferences, not provider definitions, and are never exported with a workspace.
/// </summary>
public interface IAgentModelFavoriteStore
{
    event EventHandler? Changed;

    ValueTask<ApplicationRunResult<IReadOnlyList<AgentModelFavorite>>> ListAsync(
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<Unit>> SetAsync(
        AgentModelFavorite favorite,
        bool isFavorite,
        CancellationToken cancellationToken);
}
