using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteTerminalMultiplexerStoreTests
{
    [Fact]
    public async Task PreferenceIsDisabledByDefaultAndSurvivesRestart()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteTerminalMultiplexerStore(temporary.Database);

        var initial = await store.ReadAsync(CancellationToken.None);
        Assert.True(initial.IsSuccess, initial.Error?.Message);
        Assert.Equal(TerminalMultiplexingMode.Disabled, initial.Value);

        var write = await store.WriteAsync(
            TerminalMultiplexingMode.Automatic,
            CancellationToken.None);
        Assert.True(write.IsSuccess, write.Error?.Message);
        await temporary.ReopenAsync();

        var restored = await new SqliteTerminalMultiplexerStore(temporary.Database)
            .ReadAsync(CancellationToken.None);
        Assert.Equal(TerminalMultiplexingMode.Automatic, restored.Value);
    }

    [Fact]
    public async Task ManagedSessionLeaseRoundTripsAndCanBeDeleted()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteTerminalMultiplexerStore(temporary.Database);
        var lease = new TerminalMultiplexerLease(
            new ConnectionId("ssh-prod"),
            new TerminalMultiplexerSession(
                TerminalMultiplexingMode.Automatic,
                "asura-1234abcd",
                isEstablished: true),
            TerminalMultiplexerLeaseState.TerminationPending,
            new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 10, 5, 0, TimeSpan.Zero));

        Assert.True((await store.UpsertAsync(lease, CancellationToken.None)).IsSuccess);
        await temporary.ReopenAsync();

        var reopenedStore = new SqliteTerminalMultiplexerStore(temporary.Database);
        var restored = Assert.Single((await reopenedStore
            .ListAsync(CancellationToken.None)).Value!);
        Assert.Equal(lease, restored);

        Assert.True((await reopenedStore.DeleteAsync(
            lease.ConnectionId,
            lease.Session.SessionName,
            CancellationToken.None)).IsSuccess);
        Assert.Empty((await reopenedStore.ListAsync(CancellationToken.None)).Value!);
    }
}
