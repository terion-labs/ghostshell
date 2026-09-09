using Asura.Application;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteSessionRestorePreferenceStoreTests
{
    [Fact]
    public async Task FreshProfilesRestoreSessionsByDefault()
    {
        await using var temporary = TemporaryDatabase.Create();
        var result = await new SqliteSessionRestorePreferenceStore(temporary.Database)
            .ReadAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task DisabledPreferenceSurvivesDatabaseRestart()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteSessionRestorePreferenceStore(temporary.Database);

        var write = await store.WriteAsync(false, CancellationToken.None);
        Assert.True(write.IsSuccess, write.Error?.Message);

        await temporary.ReopenAsync();
        var read = await new SqliteSessionRestorePreferenceStore(temporary.Database)
            .ReadAsync(CancellationToken.None);

        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.False(read.Value);
    }

    [Fact]
    public async Task MissingPreferenceRowFailsClosed()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM session_restore_preference;";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var store = new SqliteSessionRestorePreferenceStore(temporary.Database);
        var read = await store.ReadAsync(CancellationToken.None);
        var write = await store.WriteAsync(false, CancellationToken.None);

        Assert.Equal(ApplicationRunErrorCode.StorageFailure, read.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, write.Error!.Code);
    }
}
