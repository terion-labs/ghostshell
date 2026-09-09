using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Asura.Application;
using Asura.Core;
using StackExchange.Redis;

namespace Asura.Redis;

public sealed class RedisPanelSessionFactory(
    IDatabaseTunnelFactory? tunnelFactory = null,
    ConnectionProfile? defaultTunnel = null) : IRedisPanelSessionFactory
{
    public async Task<IRedisPanelSession> OpenAsync(
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken)
    {
        var options = RedisConnectionCatalog.Parse(connectionString);
        RedisConnectionTunnel? tunnelLease = null;
        tunnel ??= defaultTunnel;
        if (tunnel is not null)
        {
            if (tunnelFactory is null)
            {
                throw new InvalidOperationException("SSH tunneling is unavailable in this build.");
            }

            // Keep logical endpoints intact for TLS, Cluster MOVED responses,
            // and Sentinel discovery. The driver asks the tunnel for each socket.
            tunnelLease = new RedisConnectionTunnel(tunnelFactory, tunnel);
            options.Tunnel = tunnelLease;
            options.ResolveDns = false;
        }

        ConnectionMultiplexer? connection = null;
        try
        {
            connection = await ConnectionMultiplexer.ConnectAsync(options)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return await RedisPanelSession
                .CreateAsync(connection, tunnelLease, options.DefaultDatabase ?? 0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            if (tunnelLease is not null)
            {
                await tunnelLease.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }
}

internal sealed class RedisPanelSession : IRedisPanelSession
{
    private const string ReadJsonPreviewScript = """
        local json = redis.call('JSON.GET', KEYS[1], '.')
        if not json then
            return {'', 0, 0}
        end
        local length = string.len(json)
        local maximum = tonumber(ARGV[1])
        if length > maximum then
            return {string.sub(json, 1, maximum), length, 1}
        end
        return {json, length, 0}
        """;

    private const string RemoveListElementScript = """
        local current = redis.call('LINDEX', KEYS[1], ARGV[1])
        if not current or current ~= ARGV[2] then
            return 0
        end
        redis.call('LSET', KEYS[1], ARGV[1], ARGV[3])
        redis.call('LREM', KEYS[1], 1, ARGV[3])
        return 1
        """;

    private readonly ConnectionMultiplexer _connection;
    private readonly IAsyncDisposable? _tunnel;
    private readonly ISubscriber _subscriber;
    private readonly ConcurrentDictionary<RedisSubscription, RedisChannel> _subscriptions = [];
    private IDatabase _database;
    private int _databaseIndex;
    private bool _disposed;

    private RedisPanelSession(
        ConnectionMultiplexer connection,
        IAsyncDisposable? tunnel,
        int database,
        RedisServerFacts facts)
    {
        _connection = connection;
        _tunnel = tunnel;
        _databaseIndex = database;
        _database = connection.GetDatabase(database);
        _subscriber = connection.GetSubscriber();
        Facts = facts;
    }

    public RedisServerFacts Facts { get; private set; }

    public event EventHandler<RedisPubSubMessage>? MessageReceived;

    public static async Task<RedisPanelSession> CreateAsync(
        ConnectionMultiplexer connection,
        IAsyncDisposable? tunnel,
        int database,
        CancellationToken cancellationToken)
    {
        var endpoints = connection.GetEndPoints(configuredOnly: false);
        var servers = endpoints
            .Select(endpoint => connection.GetServer(endpoint))
            .Where(server => server.IsConnected)
            .ToArray();
        var primaries = ConnectedPrimaries(servers);
        if (primaries.Length == 0)
        {
            throw new RedisConnectionException(
                ConnectionFailureType.UnableToResolvePhysicalConnection,
                "Redis did not establish a connection to a usable primary. "
                + "Verify that the server is running and that the host, port, TLS, and credentials are correct.");
        }

        var topology = servers.Any(server => server.ServerType == ServerType.Cluster)
            ? RedisTopologyKind.Cluster
            : servers.Any(server => server.ServerType == ServerType.Sentinel)
                ? RedisTopologyKind.Sentinel
                : servers.Length > 0
                    ? RedisTopologyKind.Standalone
                    : RedisTopologyKind.Unknown;
        var primary = primaries[0];
        var version = primary.Version?.ToString();
        var databaseCount = topology == RedisTopologyKind.Cluster ? 1 : primary.DatabaseCount;
        var commandNames = await ReadCommandNamesAsync(primary, cancellationToken).ConfigureAwait(false);
        var facts = new RedisServerFacts(
            version,
            connection.GetStatus().Contains("RESP3", StringComparison.OrdinalIgnoreCase) ? "RESP3" : null,
            topology,
            topology == RedisTopologyKind.Cluster
                ? RedisLogicalDatabaseMode.DatabaseZeroOnly
                : RedisLogicalDatabaseMode.Selectable,
            topology == RedisTopologyKind.Cluster ? 0 : database,
            databaseCount,
            commandNames.Contains("FT.SEARCH"),
            commandNames.Contains("JSON.GET"),
            commandNames.Contains("TS.GET"),
            commandNames.Contains("SSUBSCRIBE"),
            Limitation: null);
        return new RedisPanelSession(
            connection,
            tunnel,
            facts.SelectedDatabase,
            facts);
    }

    public Task SelectDatabaseAsync(int database, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (database < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(database));
        }

        if (Facts.LogicalDatabases == RedisLogicalDatabaseMode.DatabaseZeroOnly && database != 0)
        {
            throw new NotSupportedException("Redis Cluster supports logical database zero only.");
        }

        if (Facts.ConfiguredDatabaseCount is { } count && database >= count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(database),
                $"This Redis server exposes logical databases 0 through {count - 1}.");
        }

        _databaseIndex = database;
        _database = _connection.GetDatabase(database);
        Facts = Facts with { SelectedDatabase = database };
        return Task.CompletedTask;
    }

    public async Task<RedisScanPage> ScanKeysAsync(
        string pattern,
        string? cursor,
        int count,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(count, 10, 1000);
        var serverCursor = ParseCursor(cursor);
        var servers = _connection.GetEndPoints(configuredOnly: false)
            .Select(endpoint => _connection.GetServer(endpoint))
            .ToArray();
        var primaries = ConnectedPrimaries(servers);
        if (primaries.Length == 0)
        {
            throw new RedisConnectionException(
                ConnectionFailureType.UnableToResolvePhysicalConnection,
                "The Redis primary connection was lost. Reconnect and try again.");
        }

        if (serverCursor.ServerIndex >= primaries.Length)
        {
            return new RedisScanPage([], null, IsComplete: true);
        }

        var server = primaries[serverCursor.ServerIndex];
        var result = await server.ExecuteAsync(
                _databaseIndex,
                "SCAN",
                [
                    serverCursor.Cursor,
                    "MATCH",
                    string.IsNullOrWhiteSpace(pattern) ? "*" : pattern,
                    "COUNT",
                    pageSize,
                ],
                CommandFlags.None)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var parts = (RedisResult[]?)result
            ?? throw new RedisServerException("Redis returned an invalid SCAN response.");
        if (parts.Length != 2
            || !long.TryParse(parts[0].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var next))
        {
            throw new RedisServerException("Redis returned an invalid SCAN cursor.");
        }

        var rawKeys = (RedisResult[]?)parts[1] ?? [];
        var keys = new List<RedisKeySummary>(rawKeys.Length);
        foreach (var item in rawKeys)
        {
            var bytes = (byte[]?)item ?? [];
            keys.Add(await DescribeKeyAsync((RedisKey)bytes).ConfigureAwait(false));
        }

        var serverComplete = next == 0;
        var nextServer = serverComplete ? serverCursor.ServerIndex + 1 : serverCursor.ServerIndex;
        var complete = nextServer >= primaries.Length;
        return new RedisScanPage(
            keys,
            complete ? null : $"{nextServer.ToString(CultureInfo.InvariantCulture)}:{next.ToString(CultureInfo.InvariantCulture)}",
            complete);
    }

    private static IServer[] ConnectedPrimaries(IEnumerable<IServer> servers) =>
        [.. servers
            .Where(server =>
                server.IsConnected
                && !server.IsReplica
                && server.ServerType != ServerType.Sentinel)];

    public async Task<RedisKeySnapshot> ReadKeyAsync(
        RedisKeyReference key,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        var redisKey = (RedisKey)key.Bytes;
        var summary = await DescribeKeyAsync(redisKey).ConfigureAwait(false);
        var limit = Math.Clamp(maximumEntries, 1, 5000);
        return summary.Type switch
        {
            "string" => await ReadStringAsync(redisKey, summary, limit).ConfigureAwait(false),
            "hash" => await ReadHashAsync(redisKey, summary, limit).ConfigureAwait(false),
            "list" => await ReadListAsync(redisKey, summary, limit).ConfigureAwait(false),
            "set" => await ReadSetAsync(redisKey, summary, limit).ConfigureAwait(false),
            "zset" => await ReadSortedSetAsync(redisKey, summary, limit).ConfigureAwait(false),
            "stream" => await ReadStreamAsync(redisKey, summary, limit).ConfigureAwait(false),
            "json" => await ReadJsonAsync(redisKey, summary, limit).ConfigureAwait(false),
            "timeseries" => await ReadTimeSeriesAsync(redisKey, summary, limit).ConfigureAwait(false),
            "none" => new RedisKeySnapshot(summary, 0, [], false, "The key no longer exists."),
            _ => new RedisKeySnapshot(
                summary,
                null,
                [],
                false,
                $"{summary.Type} values are not editable in this release."),
        };
    }

    public Task SetStringAsync(
        RedisKeyReference key,
        string value,
        TimeSpan? expiry,
        CancellationToken cancellationToken) =>
        WaitAsync(
            _database.StringSetAsync(
                key.Bytes,
                value,
                expiry,
                keepTtl: expiry is null,
                When.Always,
                CommandFlags.None),
            cancellationToken);

    public Task SetHashFieldAsync(
        RedisKeyReference key,
        string field,
        string value,
        CancellationToken cancellationToken) =>
        WaitAsync(_database.HashSetAsync(key.Bytes, field, value), cancellationToken);

    public Task AppendListValueAsync(
        RedisKeyReference key,
        string value,
        CancellationToken cancellationToken) =>
        WaitAsync(_database.ListRightPushAsync(key.Bytes, value), cancellationToken);

    public Task AddSetValueAsync(
        RedisKeyReference key,
        string value,
        CancellationToken cancellationToken) =>
        WaitAsync(_database.SetAddAsync(key.Bytes, value), cancellationToken);

    public Task AddSortedSetValueAsync(
        RedisKeyReference key,
        string value,
        double score,
        CancellationToken cancellationToken) =>
        WaitAsync(_database.SortedSetAddAsync(key.Bytes, value, score), cancellationToken);

    public Task AddStreamEntryAsync(
        RedisKeyReference key,
        string field,
        string value,
        CancellationToken cancellationToken) =>
        WaitAsync(_database.StreamAddAsync(key.Bytes, field, value), cancellationToken);

    public Task SetJsonAsync(
        RedisKeyReference key,
        string json,
        CancellationToken cancellationToken) =>
        WaitAsync(_database.ExecuteAsync("JSON.SET", key.Bytes, "$", json), cancellationToken);

    public Task AddTimeSeriesSampleAsync(
        RedisKeyReference key,
        double value,
        CancellationToken cancellationToken) =>
        WaitAsync(
            _database.ExecuteAsync(
                "TS.ADD",
                key.Bytes,
                "*",
                value.ToString("R", CultureInfo.InvariantCulture)),
            cancellationToken);

    public async Task<bool> DeleteKeyAsync(
        RedisKeyReference key,
        CancellationToken cancellationToken)
    {
        var result = await _database.ExecuteAsync("UNLINK", key.Bytes)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return (long)result > 0;
    }

    public Task SetExpiryAsync(
        RedisKeyReference key,
        TimeSpan? expiry,
        CancellationToken cancellationToken) =>
        WaitAsync(_database.KeyExpireAsync(key.Bytes, expiry), cancellationToken);

    public Task SetListValueAsync(
        RedisKeyReference key,
        long index,
        string value,
        CancellationToken cancellationToken) =>
        WaitAsync(_database.ListSetByIndexAsync(key.Bytes, index, value), cancellationToken);

    public async Task<RedisEntryRemovalOutcome> RemoveEntryAsync(
        RedisKeyReference key,
        string type,
        RedisValueEntry entry,
        CancellationToken cancellationToken)
    {
        var redisKey = (RedisKey)key.Bytes;
        switch (type)
        {
            case "hash":
                await WaitAsync(
                        _database.HashDeleteAsync(redisKey, entry.Field ?? entry.Identity),
                        cancellationToken)
                    .ConfigureAwait(false);
                return RedisEntryRemovalOutcome.Removed;
            case "list":
                return await RemoveListElementAsync(redisKey, entry, cancellationToken)
                    .ConfigureAwait(false);
            case "set":
                await WaitAsync(_database.SetRemoveAsync(redisKey, entry.Value), cancellationToken)
                    .ConfigureAwait(false);
                return RedisEntryRemovalOutcome.Removed;
            case "zset":
                await WaitAsync(_database.SortedSetRemoveAsync(redisKey, entry.Value), cancellationToken)
                    .ConfigureAwait(false);
                return RedisEntryRemovalOutcome.Removed;
            case "stream":
                // A stream entry is the whole field set at one id, so removing
                // any of its fields removes the entry they belong to.
                await WaitAsync(
                        _database.StreamDeleteAsync(redisKey, [StreamEntryId(entry.Identity)]),
                        cancellationToken)
                    .ConfigureAwait(false);
                return RedisEntryRemovalOutcome.Removed;
            default:
                throw new NotSupportedException(
                    $"A {type} value has no entries that can be removed one at a time.");
        }
    }

    /// <summary>
    /// Redis cannot delete a list element by position. One server-side script
    /// compares the live raw value with the snapshot, then rewrites and removes
    /// it without exposing an intermediate marker to another client.
    /// </summary>
    private async Task<RedisEntryRemovalOutcome> RemoveListElementAsync(
        RedisKey key,
        RedisValueEntry entry,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(entry.Identity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            throw new InvalidOperationException(
                $"A list element is addressed by its position, and \"{entry.Identity}\" is not one.");
        }

        if (entry.RawValue is null)
        {
            throw new InvalidOperationException(
                "The list snapshot does not contain the raw value required for safe deletion.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sentinel = $"__asura_removed_{Guid.NewGuid():N}__";
        var result = await _database.ScriptEvaluateAsync(
                RemoveListElementScript,
                [key],
                [index.ToString(CultureInfo.InvariantCulture), entry.RawValue, sentinel])
            .ConfigureAwait(false);
        return (long)result == 1
            ? RedisEntryRemovalOutcome.Removed
            : RedisEntryRemovalOutcome.Stale;
    }

    private static string StreamEntryId(string identity)
    {
        var separator = identity.LastIndexOf(':');
        return separator > 0 ? identity[..separator] : identity;
    }

    public async Task SubscribeAsync(
        RedisSubscription subscription,
        CancellationToken cancellationToken)
    {
        var channel = ToChannel(subscription);
        await _subscriber.SubscribeAsync(channel, (receivedChannel, value) =>
            MessageReceived?.Invoke(
                this,
                new RedisPubSubMessage(
                    subscription,
                    receivedChannel.ToString(),
                    Display(value),
                    DateTimeOffset.UtcNow)))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        _subscriptions[subscription] = channel;
    }

    public async Task UnsubscribeAsync(
        RedisSubscription subscription,
        CancellationToken cancellationToken)
    {
        var channel = _subscriptions.TryRemove(subscription, out var current)
            ? current
            : ToChannel(subscription);
        await _subscriber.UnsubscribeAsync(channel)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<long> PublishAsync(
        string channel,
        string payload,
        bool sharded,
        CancellationToken cancellationToken) =>
        await _subscriber.PublishAsync(
                sharded ? RedisChannel.Sharded(channel) : RedisChannel.Literal(channel),
                payload)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<RedisSearchIndex>> ListSearchIndexesAsync(
        CancellationToken cancellationToken)
    {
        if (!Facts.SearchAvailable)
        {
            return [];
        }

        var result = await _database.ExecuteAsync("FT._LIST")
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var names = (RedisResult[]?)result ?? [];
        var indexes = new List<RedisSearchIndex>(names.Length);
        foreach (var name in names.Select(item => (string?)item).Where(item => item is not null))
        {
            var info = await _database.ExecuteAsync("FT.INFO", name!)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var dictionary = info.ToDictionary();
            indexes.Add(new RedisSearchIndex(
                name!,
                dictionary.TryGetValue("index_definition", out var definition) ? definition.ToString() : null,
                dictionary.TryGetValue("attributes", out var attributes) ? attributes.ToString() : null,
                dictionary.TryGetValue("num_docs", out var documents)
                    && long.TryParse(documents.ToString(), CultureInfo.InvariantCulture, out var count)
                        ? count
                        : null));
        }

        return indexes;
    }

    public async Task<RedisSearchResult> SearchAsync(
        string index,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var result = await _database.ExecuteAsync(
                "FT.SEARCH",
                index,
                string.IsNullOrWhiteSpace(query) ? "*" : query,
                "LIMIT",
                0,
                Math.Clamp(limit, 1, 1000),
                "DIALECT",
                2)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var boundedLimit = Math.Clamp(limit, 1, 1000);
        if (result.Resp3Type == ResultType.Map)
        {
            return ParseResp3SearchResult(result, boundedLimit);
        }

        var rows = (RedisResult[]?)result ?? [];
        var total = rows.Length == 0 ? 0 : (long)rows[0];
        var values = rows.Skip(1)
            .Select((item, indexInResult) => new RedisValueEntry(
                indexInResult.ToString(CultureInfo.InvariantCulture),
                indexInResult % 2 == 0 ? "document" : "fields",
                item.ToString()))
            .ToArray();
        return new RedisSearchResult(total, values, total > boundedLimit);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _subscriber.UnsubscribeAllAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        if (_tunnel is not null)
        {
            await _tunnel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<RedisKeySummary> DescribeKeyAsync(RedisKey key)
    {
        var rawType = (await _database.ExecuteAsync("TYPE", key).ConfigureAwait(false)).ToString();
        var typeName = rawType.ToUpperInvariant() switch
        {
            "NONE" => "none",
            "STRING" => "string",
            "LIST" => "list",
            "SET" => "set",
            "ZSET" => "zset",
            "HASH" => "hash",
            "STREAM" => "stream",
            "VECTORSET" => "vectorset",
            "REJSON-RL" or "JSON" => "json",
            "TSDB-TYPE" => "timeseries",
            "MBBLOOM--" => "bloom",
            "MBTDCF--" => "cuckoo",
            "CMS-TYPE" => "count-min-sketch",
            "TOPK-TYPE" => "top-k",
            "TDIS-TYPE" => "t-digest",
            _ => string.IsNullOrWhiteSpace(rawType) ? "unknown" : rawType.ToLowerInvariant(),
        };
        var ttl = await _database.KeyTimeToLiveAsync(key).ConfigureAwait(false);
        long? memory = null;
        try
        {
            var result = await _database.ExecuteAsync("MEMORY", "USAGE", key).ConfigureAwait(false);
            memory = (long?)result;
        }
        catch (RedisServerException exception) when (IsPermissionOrUnsupported(exception))
        {
            // Metadata is optional under restricted ACLs and older servers.
        }

        return new RedisKeySummary(
            new RedisKeyReference(Display(key), (byte[])key!),
            typeName,
            ttl,
            memory);
    }

    private async Task<RedisKeySnapshot> ReadStringAsync(
        RedisKey key,
        RedisKeySummary summary,
        int limit)
    {
        var length = await _database.StringLengthAsync(key).ConfigureAwait(false);
        var value = await _database.StringGetRangeAsync(key, 0, limit - 1).ConfigureAwait(false);
        return new RedisKeySnapshot(
            summary,
            length,
            [new RedisValueEntry("value", null, Display(value))],
            length > limit);
    }

    private async Task<RedisKeySnapshot> ReadHashAsync(RedisKey key, RedisKeySummary summary, int limit)
    {
        var length = await _database.HashLengthAsync(key).ConfigureAwait(false);
        var entries = await _database.HashScanAsync(key, pageSize: limit).Take(limit).ToArrayAsync()
            .ConfigureAwait(false);
        return new RedisKeySnapshot(
            summary,
            length,
            [.. entries.Select(entry => new RedisValueEntry(Display(entry.Name), Display(entry.Name), Display(entry.Value)))],
            length > limit);
    }

    private async Task<RedisKeySnapshot> ReadListAsync(RedisKey key, RedisKeySummary summary, int limit)
    {
        var length = await _database.ListLengthAsync(key).ConfigureAwait(false);
        var values = await _database.ListRangeAsync(key, 0, limit - 1).ConfigureAwait(false);
        return new RedisKeySnapshot(
            summary,
            length,
            [.. values.Select((value, index) => new RedisValueEntry(
                index.ToString(CultureInfo.InvariantCulture),
                null,
                Display(value),
                RawValue: [.. ((byte[]?)value ?? [])]))],
            length > limit);
    }

    private async Task<RedisKeySnapshot> ReadSetAsync(RedisKey key, RedisKeySummary summary, int limit)
    {
        var length = await _database.SetLengthAsync(key).ConfigureAwait(false);
        var values = await _database.SetScanAsync(key, pageSize: limit).Take(limit).ToArrayAsync()
            .ConfigureAwait(false);
        return new RedisKeySnapshot(
            summary,
            length,
            [.. values.Select(value => new RedisValueEntry(Display(value), null, Display(value)))],
            length > limit);
    }

    private async Task<RedisKeySnapshot> ReadSortedSetAsync(RedisKey key, RedisKeySummary summary, int limit)
    {
        var length = await _database.SortedSetLengthAsync(key).ConfigureAwait(false);
        var values = await _database.SortedSetRangeByRankWithScoresAsync(key, 0, limit - 1).ConfigureAwait(false);
        return new RedisKeySnapshot(
            summary,
            length,
            [.. values.Select(value => new RedisValueEntry(Display(value.Element), null, Display(value.Element), value.Score))],
            length > limit);
    }

    private async Task<RedisKeySnapshot> ReadStreamAsync(RedisKey key, RedisKeySummary summary, int limit)
    {
        var length = await _database.StreamLengthAsync(key).ConfigureAwait(false);
        var values = await _database.StreamRangeAsync(key, count: limit).ConfigureAwait(false);
        return new RedisKeySnapshot(
            summary,
            length,
            [.. values.SelectMany(entry => entry.Values.Select(value => new RedisValueEntry(
                $"{entry.Id}:{Display(value.Name)}",
                Display(value.Name),
                Display(value.Value))))],
            length > limit);
    }

    private async Task<RedisKeySnapshot> ReadJsonAsync(RedisKey key, RedisKeySummary summary, int limit)
    {
        var maximumBytes = checked(limit * 4);
        var result = await _database.ScriptEvaluateAsync(
                ReadJsonPreviewScript,
                [key],
                [maximumBytes])
            .ConfigureAwait(false);
        var parts = (RedisResult[]?)result
            ?? throw new RedisServerException("Redis returned an invalid bounded JSON response.");
        if (parts.Length != 3
            || !long.TryParse(
                parts[1].ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var byteLength)
            || !long.TryParse(
                parts[2].ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var serverTruncated))
        {
            throw new RedisServerException("Redis returned an invalid bounded JSON response.");
        }

        var json = Encoding.UTF8.GetString((byte[]?)parts[0] ?? []);
        var value = json.Length > limit ? json[..limit] : json;
        var truncated = serverTruncated == 1 || json.Length > limit;
        return new RedisKeySnapshot(
            summary,
            byteLength,
            [new RedisValueEntry("$", "$", value)],
            truncated,
            truncated ? $"JSON preview is limited to {limit} characters." : null);
    }

    private async Task<RedisKeySnapshot> ReadTimeSeriesAsync(
        RedisKey key,
        RedisKeySummary summary,
        int limit)
    {
        var result = await _database.ExecuteAsync("TS.RANGE", key, "-", "+", "COUNT", limit)
            .ConfigureAwait(false);
        var samples = (RedisResult[]?)result ?? [];
        long? total = null;
        try
        {
            var info = (await _database.ExecuteAsync("TS.INFO", key).ConfigureAwait(false))
                .ToDictionary(StringComparer.OrdinalIgnoreCase);
            if (info.TryGetValue("totalSamples", out var totalSamples)
                && long.TryParse(totalSamples.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                total = parsed;
            }
        }
        catch (RedisServerException exception) when (IsPermissionOrUnsupported(exception))
        {
            // Sample reads remain useful when TS.INFO is restricted.
        }

        var entries = samples.Select(sample =>
        {
            var parts = (RedisResult[]?)sample ?? [];
            var timestamp = parts.Length > 0 ? parts[0].ToString() : string.Empty;
            var value = parts.Length > 1 ? parts[1].ToString() : string.Empty;
            return new RedisValueEntry(timestamp, "timestamp", value);
        }).ToArray();
        return new RedisKeySnapshot(summary, total, entries, total is > 0 && total > entries.Length);
    }

    private static async Task<HashSet<string>> ReadCommandNamesAsync(
        IServer? server,
        CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return [];
        }

        try
        {
            var result = await server.ExecuteAsync("COMMAND", "LIST")
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return ((RedisResult[]?)result ?? [])
                .Select(item => item.ToString().ToUpperInvariant())
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (RedisServerException exception) when (IsPermissionOrUnsupported(exception))
        {
            return [];
        }
    }

    private static RedisChannel ToChannel(RedisSubscription subscription) => subscription.Kind switch
    {
        RedisSubscriptionKind.Pattern => RedisChannel.Pattern(subscription.Name),
        RedisSubscriptionKind.Shard => RedisChannel.Sharded(subscription.Name),
        _ => RedisChannel.Literal(subscription.Name),
    };

    private static RedisSearchResult ParseResp3SearchResult(RedisResult result, int limit)
    {
        var response = result.ToDictionary(StringComparer.OrdinalIgnoreCase);
        var total = response.TryGetValue("total_results", out var totalResult)
            && long.TryParse(totalResult.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTotal)
                ? parsedTotal
                : 0;
        var documents = response.TryGetValue("results", out var resultRows)
            ? (RedisResult[]?)resultRows ?? []
            : [];
        var values = documents.Select((document, index) =>
        {
            var fields = document.ToDictionary(StringComparer.OrdinalIgnoreCase);
            var identity = fields.TryGetValue("id", out var id)
                ? id.ToString()
                : index.ToString(CultureInfo.InvariantCulture);
            var attributes = fields.TryGetValue("extra_attributes", out var extraAttributes)
                ? FormatSearchAttributes(extraAttributes)
                : document.ToString();
            return new RedisValueEntry(identity, "document", attributes);
        }).ToArray();
        return new RedisSearchResult(total, values, total > limit);
    }

    private static string FormatSearchAttributes(RedisResult result)
    {
        var attributes = result.ToDictionary(StringComparer.OrdinalIgnoreCase);
        return string.Join(
            ", ",
            attributes.Select(attribute => $"{attribute.Key}={attribute.Value}"));
    }

    private static (int ServerIndex, long Cursor) ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return (0, 0);
        }

        var parts = cursor.Split(':', 2);
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var server)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? (server, value)
                : throw new ArgumentException("The Redis scan cursor is invalid.", nameof(cursor));
    }

    private static bool IsPermissionOrUnsupported(RedisServerException exception) =>
        exception.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase);

    private static string Display(RedisValue value) => value.IsNull
        ? "(nil)"
        : Encoding.UTF8.GetString((byte[]?)value ?? []);

    private static string Display(RedisKey key) => Display((RedisValue)(byte[]?)key);

    private static async Task WaitAsync(Task task, CancellationToken cancellationToken) =>
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
}
