using GhostShell.Application;

namespace GhostShell.ConnectionBackend;

internal sealed partial class RedisWorkspaceSession
{
    public async Task SelectDatabaseAsync(int database, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.SelectDatabase, Count: database), cancellationToken).ConfigureAwait(false);

    public async Task<RedisScanPage> ScanKeysAsync(string pattern, string? cursor, int count, CancellationToken cancellationToken) =>
        (await InvokeAsync(new(0, RedisWorkspaceOperation.ScanKeys, Text: pattern, OtherText: cursor, Count: count), cancellationToken).ConfigureAwait(false)).Scan
        ?? throw new InvalidDataException("The Redis backend omitted the scan result.");

    public async Task<RedisKeySnapshot> ReadKeyAsync(RedisKeyReference key, int maximumEntries, CancellationToken cancellationToken) =>
        (await InvokeAsync(new(0, RedisWorkspaceOperation.ReadKey, Key: key, Count: maximumEntries), cancellationToken).ConfigureAwait(false)).Key
        ?? throw new InvalidDataException("The Redis backend omitted the key result.");

    public async Task SetStringAsync(RedisKeyReference key, string value, TimeSpan? expiry, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.SetString, Key: key, Text: value, Expiry: expiry), cancellationToken).ConfigureAwait(false);

    public async Task SetHashFieldAsync(RedisKeyReference key, string field, string value, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.SetHashField, Key: key, Text: field, OtherText: value), cancellationToken).ConfigureAwait(false);

    public async Task AppendListValueAsync(RedisKeyReference key, string value, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.AppendListValue, Key: key, Text: value), cancellationToken).ConfigureAwait(false);

    public async Task SetListValueAsync(RedisKeyReference key, long index, string value, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.SetListValue, Key: key, Text: value, Index: index), cancellationToken).ConfigureAwait(false);

    public async Task AddSetValueAsync(RedisKeyReference key, string value, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.AddSetValue, Key: key, Text: value), cancellationToken).ConfigureAwait(false);

    public async Task AddSortedSetValueAsync(RedisKeyReference key, string value, double score, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.AddSortedSetValue, Key: key, Text: value, Number: score), cancellationToken).ConfigureAwait(false);

    public async Task AddStreamEntryAsync(RedisKeyReference key, string field, string value, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.AddStreamEntry, Key: key, Text: field, OtherText: value), cancellationToken).ConfigureAwait(false);

    public async Task SetJsonAsync(RedisKeyReference key, string json, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.SetJson, Key: key, Text: json), cancellationToken).ConfigureAwait(false);

    public async Task AddTimeSeriesSampleAsync(RedisKeyReference key, double value, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.AddTimeSeriesSample, Key: key, Number: value), cancellationToken).ConfigureAwait(false);

    public async Task<bool> DeleteKeyAsync(RedisKeyReference key, CancellationToken cancellationToken) =>
        (await InvokeAsync(new(0, RedisWorkspaceOperation.DeleteKey, Key: key), cancellationToken).ConfigureAwait(false)).Deleted;

    public async Task<RedisEntryRemovalOutcome> RemoveEntryAsync(RedisKeyReference key, string type, RedisValueEntry entry, CancellationToken cancellationToken) =>
        (await InvokeAsync(new(0, RedisWorkspaceOperation.RemoveEntry, Key: key, Text: type, Entry: entry), cancellationToken).ConfigureAwait(false)).Removal;

    public async Task SetExpiryAsync(RedisKeyReference key, TimeSpan? expiry, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.SetExpiry, Key: key, Expiry: expiry), cancellationToken).ConfigureAwait(false);

    public async Task SubscribeAsync(RedisSubscription subscription, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.Subscribe, Subscription: subscription), cancellationToken).ConfigureAwait(false);

    public async Task UnsubscribeAsync(RedisSubscription subscription, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.Unsubscribe, Subscription: subscription), cancellationToken).ConfigureAwait(false);

    public async Task<long> PublishAsync(string channel, string payload, bool sharded, CancellationToken cancellationToken) =>
        (await InvokeAsync(new(0, RedisWorkspaceOperation.Publish, Text: channel, OtherText: payload, Sharded: sharded), cancellationToken).ConfigureAwait(false)).Count;

    public async Task<IReadOnlyList<RedisSearchIndex>> ListSearchIndexesAsync(CancellationToken cancellationToken) =>
        (await InvokeAsync(new(0, RedisWorkspaceOperation.ListSearchIndexes), cancellationToken).ConfigureAwait(false)).Indexes
        ?? throw new InvalidDataException("The Redis backend omitted the search indexes.");

    public async Task<RedisSearchResult> SearchAsync(string index, string query, int limit, CancellationToken cancellationToken) =>
        (await InvokeAsync(new(0, RedisWorkspaceOperation.Search, Text: index, OtherText: query, Count: limit), cancellationToken).ConfigureAwait(false)).Search
        ?? throw new InvalidDataException("The Redis backend omitted the search result.");
}
