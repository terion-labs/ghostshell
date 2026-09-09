using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteAgentModelFavoriteStoreTests
{
    [Fact]
    public async Task FavoriteSurvivesDatabaseReopenAndCanBeRemoved()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentModelFavoriteStore(
            temporary.Database,
            TimeProvider.System);
        var favorite = new AgentModelFavorite(
            new AiProviderProfileId("openai"),
            "gpt-5.6-terra");
        var changeCount = 0;
        store.Changed += (_, _) => changeCount++;

        Assert.True((await store.SetAsync(
            favorite,
            isFavorite: true,
            CancellationToken.None)).IsSuccess);
        await temporary.ReopenAsync();
        store = new SqliteAgentModelFavoriteStore(
            temporary.Database,
            TimeProvider.System);

        var favorites = Success(await store.ListAsync(CancellationToken.None));
        Assert.Equal(favorite, Assert.Single(favorites));

        Assert.True((await store.SetAsync(
            favorite,
            isFavorite: false,
            CancellationToken.None)).IsSuccess);
        Assert.Empty(Success(await store.ListAsync(CancellationToken.None)));
        Assert.Equal(1, changeCount);
    }

    private static T Success<T>(ApplicationRunResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return Assert.IsAssignableFrom<T>(result.Value);
    }
}
