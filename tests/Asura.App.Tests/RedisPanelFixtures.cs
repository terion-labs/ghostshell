using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

/// <summary>
/// A Redis server the panel tests can drive: one string key, a clock the test
/// moves by hand, and whichever capabilities the case under test needs.
/// </summary>
internal static class RedisPanelFixtures
{
    internal static RedisRuntimePanelViewModel Panel(
        StubSession session,
        TimeProvider? clock = null) =>
        new(
            PanelInstanceId.New(),
            "Redis",
            new StubFactory(session),
            new StubCatalog(),
            connectionString: "localhost:6379",
            timeProvider: clock ?? TimeProvider.System);

    internal sealed class StoppedClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    internal sealed class StubFactory(StubSession session) : IRedisPanelSessionFactory
    {
        public Task<IRedisPanelSession> OpenAsync(
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken) =>
            Task.FromResult<IRedisPanelSession>(session);
    }

    internal sealed class StubCatalog : IDatabaseConnectionCatalog
    {
        public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } = [RedisDatabase.Descriptor];

        public DatabaseConnectionDetails ParseConnectionDetails(string driverId, string connectionString) =>
            new("localhost", 6379);

        public string BuildConnectionString(string driverId, DatabaseConnectionDetails details) =>
            "localhost:6379";
    }

    /// <summary>A server holding one string key whose TTL the test moves.</summary>
    internal sealed class StubSession(
        TimeSpan? timeToLive,
        bool searchAvailable = false,
        string type = "string") : IRedisPanelSession
    {
        private static readonly RedisKeyReference Key =
            new("session:9f3c1a", System.Text.Encoding.UTF8.GetBytes("session:9f3c1a"));

        public TimeSpan? TimeToLive { get; set; } = timeToLive;

        public RedisEntryRemovalOutcome RemovalOutcome { get; set; } =
            RedisEntryRemovalOutcome.Removed;

        public int ReadCount { get; private set; }

        public RedisServerFacts Facts { get; } = new(
            "7.4.1",
            "RESP3",
            RedisTopologyKind.Standalone,
            RedisLogicalDatabaseMode.Selectable,
            SelectedDatabase: 0,
            ConfiguredDatabaseCount: 16,
            SearchAvailable: searchAvailable,
            JsonAvailable: false,
            TimeSeriesAvailable: false,
            ShardedPubSubAvailable: false);

        public event EventHandler<RedisPubSubMessage>? MessageReceived;

        public Task SelectDatabaseAsync(int database, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<RedisScanPage> ScanKeysAsync(
            string pattern,
            string? cursor,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RedisScanPage([Describe()], null, true));

        public Task<RedisKeySnapshot> ReadKeyAsync(
            RedisKeyReference key,
            int maximumEntries,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(new RedisKeySnapshot(
                Describe(),
                1,
                [Entry()],
                Truncated: false));
        }

        /// <summary>
        /// One entry, addressed the way the real session addresses that type:
        /// a hash by field, a list by position, a set or sorted set by the
        /// member itself, a stream by its id and field.
        /// </summary>
        private RedisValueEntry Entry() => type switch
        {
            "hash" => new("email", "email", "value"),
            "list" => new("0", null, "value", RawValue: "value"u8.ToArray()),
            "set" => new("value", null, "value"),
            "zset" => new("value", null, "value", 1),
            "stream" => new("1-0:event", "event", "value"),
            _ => new(type, null, "value"),
        };

        public Task SetStringAsync(RedisKeyReference key, string value, TimeSpan? expiry, CancellationToken cancellationToken)
        {
            if (expiry is not null)
            {
                TimeToLive = expiry;
            }

            return Task.CompletedTask;
        }

        /// <summary>Every entry written, in the order the panel sent it.</summary>
        public List<string> Writes { get; } = [];

        public Task SetHashFieldAsync(RedisKeyReference key, string field, string value, CancellationToken cancellationToken)
        {
            Writes.Add($"hash {field}={value}");
            return Task.CompletedTask;
        }

        public Task AppendListValueAsync(RedisKeyReference key, string value, CancellationToken cancellationToken)
        {
            Writes.Add($"list {value}");
            return Task.CompletedTask;
        }

        public Task SetListValueAsync(RedisKeyReference key, long index, string value, CancellationToken cancellationToken)
        {
            Writes.Add($"list[{index}]={value}");
            return Task.CompletedTask;
        }

        public Task AddSetValueAsync(RedisKeyReference key, string value, CancellationToken cancellationToken)
        {
            Writes.Add($"set {value}");
            return Task.CompletedTask;
        }

        public Task AddSortedSetValueAsync(RedisKeyReference key, string value, double score, CancellationToken cancellationToken)
        {
            Writes.Add($"zset {value}@{score.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return Task.CompletedTask;
        }
        public Task AddStreamEntryAsync(RedisKeyReference key, string field, string value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetJsonAsync(RedisKeyReference key, string json, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AddTimeSeriesSampleAsync(RedisKeyReference key, double value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> DeleteKeyAsync(RedisKeyReference key, CancellationToken cancellationToken) => Task.FromResult(true);

        /// <summary>Every entry the panel asked to have removed.</summary>
        public List<string> Removals { get; } = [];

        public Task<RedisEntryRemovalOutcome> RemoveEntryAsync(
            RedisKeyReference key,
            string type,
            RedisValueEntry entry,
            CancellationToken cancellationToken)
        {
            Removals.Add($"{type} {entry.Field ?? entry.Identity}");
            return Task.FromResult(RemovalOutcome);
        }
        /// <summary>Every deadline the panel asked for, newest last.</summary>
        public List<TimeSpan?> ExpiryWrites { get; } = [];

        public Task SetExpiryAsync(RedisKeyReference key, TimeSpan? expiry, CancellationToken cancellationToken)
        {
            ExpiryWrites.Add(expiry);
            TimeToLive = expiry;
            return Task.CompletedTask;
        }
        public Task SubscribeAsync(RedisSubscription subscription, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UnsubscribeAsync(RedisSubscription subscription, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<long> PublishAsync(string channel, string payload, bool sharded, CancellationToken cancellationToken) => Task.FromResult(0L);

        public Task<IReadOnlyList<RedisSearchIndex>> ListSearchIndexesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RedisSearchIndex>>([]);

        public Task<RedisSearchResult> SearchAsync(string index, string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new RedisSearchResult(0, [], false));

        public ValueTask DisposeAsync()
        {
            _ = MessageReceived;
            return ValueTask.CompletedTask;
        }

        private RedisKeySummary Describe() => new(Key, type, TimeToLive, 40);
    }
}
