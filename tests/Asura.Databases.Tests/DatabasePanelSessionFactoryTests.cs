using System.Reflection;
using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Databases.Tests;

public sealed class DatabasePanelSessionFactoryTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"asura-hosted-database-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(
            $"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL);"
            + "INSERT INTO widgets (name) VALUES ('alpha'), ('beta');";
        _ = await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RelationalSessionUsesOpaqueObjectsAndBoundedReads()
    {
        await using var client = new DatabasePanelClient();
        var factory = new DatabasePanelSessionFactory(
            client,
            TimeProvider.System);
        var target = new DatabaseSessionTarget(
            "sqlite",
            $"Data Source={_databasePath}",
            "saved-database-1",
            7);
        await using var session = await factory.CreateAsync(
            new SessionId("database-session"),
            target,
            CancellationToken.None);
        var relational = Assert.IsAssignableFrom<IRelationalDatabasePanelSession>(session);

        Assert.Equal(PanelKind.DatabaseViewer, relational.Kind);
        Assert.Equal(DatabasePanelBackend.Relational, relational.State.Backend);
        Assert.Equal("SQLite", relational.State.DisplayName);
        Assert.DoesNotContain(_databasePath, relational.State.ToString());
        Assert.DoesNotContain(_databasePath, JsonSerializer.Serialize(target));

        var objects = await relational.ListObjectsAsync(20, CancellationToken.None);
        var widget = Assert.Single(objects.Objects, item => string.Equals(item.Name, "widgets", StringComparison.Ordinal));
        Assert.DoesNotContain("widgets", widget.Reference.Value, StringComparison.OrdinalIgnoreCase);
        var details = await relational.DescribeObjectAsync(
            widget.Reference,
            CancellationToken.None);
        Assert.Equal(["id", "name"], details.Columns.Select(column => column.Name), StringComparer.Ordinal);

        var page = await relational.ReadTableAsync(
            new DatabaseTableReadRequest(
                widget.Reference,
                new DatabaseTableQuery(
                    [],
                    [new DatabaseSort("id")],
                    Offset: 0,
                    Limit: 10)),
            CancellationToken.None);
        Assert.Equal(2, page.Page.Result.Rows.Count);
        Assert.Equal("alpha", page.Page.Result.Rows[0][1]);

        var graph = await relational.ReadSchemaGraphAsync(20, CancellationToken.None);
        Assert.Contains(graph.Tables, table => string.Equals(table.Object.Name, "widgets", StringComparison.Ordinal));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            relational.DescribeObjectAsync(
                    new DatabaseObjectReference("opaque_but_unknown"),
                    CancellationToken.None)
                .AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            relational.ListObjectsAsync(501, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RedisSessionLeasesKeysAndRemovesUnsupportedSearchCapability()
    {
        await using var relational = new DatabasePanelClient();
        var redisFactory = new FakeRedisFactory();
        var factory = new DatabasePanelSessionFactory(
            relational,
            TimeProvider.System,
            redisFactory);
        var target = new DatabaseSessionTarget(
            RedisDatabase.DriverId,
            "host=redis.internal,password=not-for-output",
            "saved-redis-1",
            3,
            credentialReference: new SecretRef("redis-secret"));
        var session = await factory.CreateAsync(
            new SessionId("redis-session"),
            target,
            CancellationToken.None);
        var redis = Assert.IsAssignableFrom<IRedisDatabasePanelSession>(session);

        Assert.False(redis.Capabilities.Contains(SessionCapabilities.RedisSearch));
        Assert.False(redis.Capabilities.Contains(SessionCapabilities.RedisListIndexes));
        Assert.Equal(DatabasePanelBackend.Redis, redis.State.Backend);
        Assert.DoesNotContain("not-for-output", JsonSerializer.Serialize(redis.State));
        Assert.DoesNotContain("not-for-output", JsonSerializer.Serialize(target));

        var page = await redis.ScanAsync("widget:*", null, 25, CancellationToken.None);
        var key = Assert.Single(page.Keys);
        Assert.Equal("widget:1", key.DisplayName);
        Assert.DoesNotContain("widget", key.Reference.Value, StringComparison.OrdinalIgnoreCase);
        var value = await redis.ReadAsync(
            new RedisKeyReadRequest(key.Reference, 10),
            CancellationToken.None);
        Assert.Equal("alpha", Assert.Single(value.Entries).Value);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            redis.ReadAsync(
                    new RedisKeyReadRequest(
                        new RedisKeyReferenceId("opaque_but_unknown"),
                        10),
                    CancellationToken.None)
                .AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            redis.SearchAsync("index", "*", 10, CancellationToken.None).AsTask());

        await session.DisposeAsync();
        Assert.Equal(1, redisFactory.Session.DisposeCount);
    }

    [Fact]
    public async Task RedisSearchIndexDiscoveryUsesTheLiveCapabilityAndBoundedPage()
    {
        await using var relational = new DatabasePanelClient();
        var redisFactory = new FakeRedisFactory();
        redisFactory.Session.SearchAvailable = true;
        redisFactory.Session.SearchIndexes =
        [
            new RedisSearchIndex("users", "ON HASH", "name TEXT", 12),
            new RedisSearchIndex("orders", "ON JSON", "$.total NUMERIC", 7),
        ];
        var factory = new DatabasePanelSessionFactory(
            relational,
            TimeProvider.System,
            redisFactory);
        await using var session = await factory.CreateAsync(
            new SessionId("redis-search-index-session"),
            new DatabaseSessionTarget(
                RedisDatabase.DriverId,
                "host=redis.internal,password=not-for-output",
                "saved-redis-search-index",
                1,
                credentialReference: new SecretRef("redis-search-index-secret")),
            CancellationToken.None);
        var redis = Assert.IsAssignableFrom<IRedisDatabasePanelSession>(session);

        Assert.True(redis.Capabilities.Contains(SessionCapabilities.RedisListIndexes));
        var page = await redis.ListSearchIndexesAsync(1, CancellationToken.None);
        Assert.Equal("users", Assert.Single(page.Indexes).Name);
        Assert.True(page.IsTruncated);
    }

    [Fact]
    public async Task RedisSessionClipsHostileValuesAndRejectsInvalidUnicode()
    {
        await using var relational = new DatabasePanelClient();
        var redisFactory = new FakeRedisFactory();
        redisFactory.Session.EntryValue = string.Concat(
            Enumerable.Repeat("🙂", 20_000));
        var factory = new DatabasePanelSessionFactory(
            relational,
            TimeProvider.System,
            redisFactory);
        var session = await factory.CreateAsync(
            new SessionId("redis-hostile-session"),
            new DatabaseSessionTarget(
                RedisDatabase.DriverId,
                "host=redis.internal,password=not-for-output",
                "saved-redis-hostile",
                1,
                credentialReference: new SecretRef("redis-hostile-secret")),
            CancellationToken.None);
        var redis = Assert.IsAssignableFrom<IRedisDatabasePanelSession>(session);
        var key = Assert.Single((await redis.ScanAsync(
            "*",
            null,
            1,
            CancellationToken.None)).Keys);

        var value = await redis.ReadAsync(
            new RedisKeyReadRequest(key.Reference, 1),
            CancellationToken.None);
        var entry = Assert.Single(value.Entries);

        Assert.True(value.IsTruncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(entry.Value) <= 16 * 1_024);
        Assert.DoesNotContain('\uFFFD', entry.Value);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(value).Length <= 64 * 1_024);

        redisFactory.Session.EntryValue = new string(['b', 'a', 'd', '\uD800']);
        await Assert.ThrowsAsync<InvalidDataException>(() => redis.ReadAsync(
            new RedisKeyReadRequest(key.Reference, 1),
            CancellationToken.None).AsTask());
        await session.DisposeAsync();
    }

    [Fact]
    public async Task RelationalDescribeRejectsProviderObjectSubstitution()
    {
        var client = DispatchProxy.Create<
            IDatabasePanelClient,
            SubstitutingDatabaseClient>();
        var factory = new DatabasePanelSessionFactory(client, TimeProvider.System);
        await using var session = await factory.CreateAsync(
            new SessionId("substitution-session"),
            new DatabaseSessionTarget(
                "fake",
                "trusted-secret-boundary",
                "saved-substitution",
                1),
            CancellationToken.None);
        var relational = Assert.IsAssignableFrom<IRelationalDatabasePanelSession>(session);
        var reference = Assert.Single((await relational.ListObjectsAsync(
            10,
            CancellationToken.None)).Objects).Reference;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            relational.DescribeObjectAsync(reference, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RedisReadRejectsProviderKeySubstitution()
    {
        await using var relational = new DatabasePanelClient();
        var redisFactory = new FakeRedisFactory();
        var factory = new DatabasePanelSessionFactory(
            relational,
            TimeProvider.System,
            redisFactory);
        var session = await factory.CreateAsync(
            new SessionId("redis-substitution-session"),
            new DatabaseSessionTarget(
                RedisDatabase.DriverId,
                "host=redis.internal,password=not-for-output",
                "saved-redis-substitution",
                1,
                credentialReference: new SecretRef("redis-substitution-secret")),
            CancellationToken.None);
        var redis = Assert.IsAssignableFrom<IRedisDatabasePanelSession>(session);
        var reference = Assert.Single((await redis.ScanAsync(
            "*",
            null,
            1,
            CancellationToken.None)).Keys).Reference;
        redisFactory.Session.SubstituteKey = true;

        await Assert.ThrowsAsync<InvalidDataException>(() => redis.ReadAsync(
            new RedisKeyReadRequest(reference, 1),
            CancellationToken.None).AsTask());
        await session.DisposeAsync();
    }

    private sealed class FakeRedisFactory : IRedisPanelSessionFactory
    {
        public FakeRedisSession Session { get; } = new();

        public Task<IRedisPanelSession> OpenAsync(
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains("redis.internal", connectionString);
            return Task.FromResult<IRedisPanelSession>(Session);
        }
    }

    private sealed class FakeRedisSession : IRedisPanelSession
    {
        private static readonly RedisKeyReference Key = new(
            "widget:1",
            "widget:1"u8.ToArray());

        public RedisServerFacts Facts => new(
            "8.0",
            "RESP3",
            RedisTopologyKind.Standalone,
            RedisLogicalDatabaseMode.Selectable,
            0,
            16,
            SearchAvailable,
            JsonAvailable: false,
            TimeSeriesAvailable: false,
            ShardedPubSubAvailable: false);

        public bool SearchAvailable { get; set; }

        public IReadOnlyList<RedisSearchIndex> SearchIndexes { get; set; } = [];

        public int DisposeCount { get; private set; }

        public string EntryValue { get; set; } = "alpha";

        public bool SubstituteKey { get; set; }

        public event EventHandler<RedisPubSubMessage>? MessageReceived
        {
            add { }
            remove { }
        }

        public Task<RedisScanPage> ScanKeysAsync(
            string pattern,
            string? cursor,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RedisScanPage(
                [new RedisKeySummary(Key, "string", null, 12)],
                null,
                IsComplete: true));

        public Task<RedisKeySnapshot> ReadKeyAsync(
            RedisKeyReference key,
            int maximumEntries,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Key.Bytes, key.Bytes);
            var returnedKey = SubstituteKey
                ? new RedisKeyReference("other:1", "other:1"u8.ToArray())
                : Key;
            return Task.FromResult(new RedisKeySnapshot(
                new RedisKeySummary(returnedKey, "string", null, 12),
                5,
                [new RedisValueEntry("value", null, EntryValue)],
                Truncated: false));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public Task SelectDatabaseAsync(int database, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetStringAsync(RedisKeyReference key, string value, TimeSpan? expiry, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetHashFieldAsync(RedisKeyReference key, string field, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AppendListValueAsync(RedisKeyReference key, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetListValueAsync(RedisKeyReference key, long index, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AddSetValueAsync(RedisKeyReference key, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AddSortedSetValueAsync(RedisKeyReference key, string value, double score, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AddStreamEntryAsync(RedisKeyReference key, string field, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetJsonAsync(RedisKeyReference key, string json, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AddTimeSeriesSampleAsync(RedisKeyReference key, double value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> DeleteKeyAsync(RedisKeyReference key, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<RedisEntryRemovalOutcome> RemoveEntryAsync(RedisKeyReference key, string type, RedisValueEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult(RedisEntryRemovalOutcome.Removed);

        public Task SetExpiryAsync(RedisKeyReference key, TimeSpan? expiry, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SubscribeAsync(RedisSubscription subscription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UnsubscribeAsync(RedisSubscription subscription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<long> PublishAsync(string channel, string payload, bool sharded, CancellationToken cancellationToken) =>
            Task.FromResult(0L);

        public Task<IReadOnlyList<RedisSearchIndex>> ListSearchIndexesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SearchIndexes);

        public Task<RedisSearchResult> SearchAsync(string index, string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new RedisSearchResult(0, [], false));
    }

    public class SubstitutingDatabaseClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Drivers" => new[]
                {
                    new DatabaseDriverDescriptor(
                        "fake",
                        "Fake",
                        "trusted boundary"),
                },
                nameof(IDatabasePanelClient.ListTablesAsync) =>
                    Task.FromResult<IReadOnlyList<DatabaseTableDescriptor>>(
                        [new DatabaseTableDescriptor(
                            "widgets",
                            DatabaseTableKind.Table,
                            "catalog",
                            "public")]),
                nameof(IDatabasePanelClient.GetObjectDetailsAsync) =>
                    Task.FromResult(new DatabaseObjectDetails(
                        new DatabaseTableDescriptor(
                            "secrets",
                            DatabaseTableKind.Table,
                            "catalog",
                            "private"),
                        [],
                        [],
                        CanEdit: false)),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
    }
}
