using Asura.Core;

namespace Asura.Application;

public static class RedisDatabase
{
    public const string DriverId = "redis";

    public static DatabaseDriverDescriptor Descriptor { get; } = new(
        DriverId,
        "Redis",
        "host:port,ssl=false,defaultDatabase=0",
        DefaultPort: 6379,
        DatabaseLabel: "Logical database",
        CanListDatabases: true);
}

public enum RedisTopologyKind
{
    Standalone,
    Sentinel,
    Cluster,
    Unknown,
}

public enum RedisLogicalDatabaseMode
{
    Selectable,
    DatabaseZeroOnly,
    Unknown,
}

public sealed record RedisServerFacts(
    string? Version,
    string? Protocol,
    RedisTopologyKind Topology,
    RedisLogicalDatabaseMode LogicalDatabases,
    int SelectedDatabase,
    int? ConfiguredDatabaseCount,
    bool SearchAvailable,
    bool JsonAvailable,
    bool TimeSeriesAvailable,
    bool ShardedPubSubAvailable,
    string? Limitation = null);

public sealed record RedisKeyReference(string DisplayName, byte[] Bytes);

public sealed record RedisKeySummary(
    RedisKeyReference Key,
    string Type,
    TimeSpan? TimeToLive,
    long? MemoryBytes);

/// <summary>
/// An opaque scan position. Callers persist only the token and never infer
/// ordering or totals from it; Redis SCAN is a live cursor, not pagination.
/// </summary>
public sealed record RedisScanPage(
    IReadOnlyList<RedisKeySummary> Keys,
    string? NextCursor,
    bool IsComplete);

public sealed record RedisValueEntry(
    string Identity,
    string? Field,
    string Value,
    double? Score = null,
    byte[]? RawValue = null);

public enum RedisEntryRemovalOutcome
{
    Removed,
    Stale,
}

public sealed record RedisKeySnapshot(
    RedisKeySummary Summary,
    long? Length,
    IReadOnlyList<RedisValueEntry> Entries,
    bool Truncated,
    string? Limitation = null);

public enum RedisSubscriptionKind
{
    Channel,
    Pattern,
    Shard,
}

public sealed record RedisSubscription(
    RedisSubscriptionKind Kind,
    string Name);

public sealed record RedisPubSubMessage(
    RedisSubscription Subscription,
    string Channel,
    string Payload,
    DateTimeOffset ReceivedAt);

public sealed record RedisSearchIndex(
    string Name,
    string? Definition,
    string? Attributes,
    long? DocumentCount);

public sealed record RedisSearchResult(
    long Total,
    IReadOnlyList<RedisValueEntry> Values,
    bool Truncated);

/// <summary>
/// A live Redis connection. Unlike the relational database client this object
/// owns reconnect state and subscriptions and therefore lives with one panel.
/// </summary>
public interface IRedisPanelSession : IAsyncDisposable
{
    RedisServerFacts Facts { get; }

    event EventHandler<RedisPubSubMessage>? MessageReceived;

    Task SelectDatabaseAsync(int database, CancellationToken cancellationToken);

    Task<RedisScanPage> ScanKeysAsync(
        string pattern,
        string? cursor,
        int count,
        CancellationToken cancellationToken);

    Task<RedisKeySnapshot> ReadKeyAsync(
        RedisKeyReference key,
        int maximumEntries,
        CancellationToken cancellationToken);

    Task SetStringAsync(
        RedisKeyReference key,
        string value,
        TimeSpan? expiry,
        CancellationToken cancellationToken);

    Task SetHashFieldAsync(
        RedisKeyReference key,
        string field,
        string value,
        CancellationToken cancellationToken);

    Task AppendListValueAsync(
        RedisKeyReference key,
        string value,
        CancellationToken cancellationToken);

    /// <summary>Rewrites the element already at a position in a list.</summary>
    Task SetListValueAsync(
        RedisKeyReference key,
        long index,
        string value,
        CancellationToken cancellationToken);

    Task AddSetValueAsync(
        RedisKeyReference key,
        string value,
        CancellationToken cancellationToken);

    Task AddSortedSetValueAsync(
        RedisKeyReference key,
        string value,
        double score,
        CancellationToken cancellationToken);

    Task AddStreamEntryAsync(
        RedisKeyReference key,
        string field,
        string value,
        CancellationToken cancellationToken);

    Task SetJsonAsync(
        RedisKeyReference key,
        string json,
        CancellationToken cancellationToken);

    Task AddTimeSeriesSampleAsync(
        RedisKeyReference key,
        double value,
        CancellationToken cancellationToken);

    Task<bool> DeleteKeyAsync(
        RedisKeyReference key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes one entry from a collection: a hash field, a list element, a set
    /// or sorted-set member, or a stream entry. Redis addresses each of those
    /// differently — and a list not at all, except by rewriting the element it
    /// is at — so the type is stated rather than inferred.
    /// </summary>
    Task<RedisEntryRemovalOutcome> RemoveEntryAsync(
        RedisKeyReference key,
        string type,
        RedisValueEntry entry,
        CancellationToken cancellationToken);

    Task SetExpiryAsync(
        RedisKeyReference key,
        TimeSpan? expiry,
        CancellationToken cancellationToken);

    Task SubscribeAsync(
        RedisSubscription subscription,
        CancellationToken cancellationToken);

    Task UnsubscribeAsync(
        RedisSubscription subscription,
        CancellationToken cancellationToken);

    Task<long> PublishAsync(
        string channel,
        string payload,
        bool sharded,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RedisSearchIndex>> ListSearchIndexesAsync(
        CancellationToken cancellationToken);

    Task<RedisSearchResult> SearchAsync(
        string index,
        string query,
        int limit,
        CancellationToken cancellationToken);
}

public interface IRedisPanelSessionFactory
{
    Task<IRedisPanelSession> OpenAsync(
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken);
}
