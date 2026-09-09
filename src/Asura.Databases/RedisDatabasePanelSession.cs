using Asura.Application;
using Asura.Core;

namespace Asura.Databases;

internal sealed partial class RedisDatabasePanelSession : IRedisDatabasePanelSession
{
    public const int MaximumScanCount = 500;
    public const int MaximumEntries = 500;
    public const int MaximumSearchResults = 100;
    public const int MaximumSearchIndexes = 100;
    public const int MaximumPatternLength = 512;
    public const int MaximumQueryLength = 4_096;

    private readonly DatabaseOpaqueReferencePool<RedisKeyLease> _keys = new();
    private readonly DatabasePanelSessionLifetime _lifetime;
    private readonly IRedisPanelSession _redis;
    private int _disposed;

    public RedisDatabasePanelSession(
        SessionId id,
        DatabaseSessionBinding binding,
        IRedisPanelSession redis,
        CapabilitySet advertisedCapabilities,
        TimeProvider timeProvider)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        var capabilities = redis.Facts.SearchAvailable
            ? advertisedCapabilities
            : new CapabilitySet(advertisedCapabilities.Values.Where(value =>
                value is not (
                    SessionCapabilities.RedisSearch
                    or SessionCapabilities.RedisListIndexes)));
        var safeFacts = ProjectFacts(redis.Facts);
        State = new DatabasePanelSessionState(
            DatabasePanelBackend.Redis,
            RedisDatabase.DriverId,
            "Redis",
            IsReady: true,
            ServerVersion: safeFacts.Version,
            Redis: safeFacts);
        _lifetime = new DatabasePanelSessionLifetime(
            id,
            capabilities,
            "Redis keyspace is ready.",
            timeProvider);
    }

    public SessionId Id => _lifetime.Id;

    public PanelKind Kind => PanelKind.DatabaseViewer;

    public CapabilitySet Capabilities => _lifetime.Capabilities;

    public DatabaseSessionBinding Binding { get; }

    public DatabasePanelSessionState State { get; }

    public async ValueTask<RedisKeyPage> ScanAsync(
        string pattern,
        string? cursor,
        int count,
        CancellationToken cancellationToken)
    {
        RequireOpen();
        pattern ??= "*";
        if (pattern.Length > MaximumPatternLength || pattern.Any(char.IsControl))
        {
            throw new ArgumentException("The Redis pattern is invalid.", nameof(pattern));
        }

        if (cursor is { Length: > 256 }
            || cursor?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("The Redis cursor is invalid.", nameof(cursor));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumScanCount);
        using var operation = _lifetime.CreateOperationCancellation(cancellationToken);
        var page = await _redis
            .ScanKeysAsync(pattern, cursor, count, operation.Token)
            .ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        if (page.Keys.Count > count)
        {
            throw new InvalidDataException(
                "The Redis provider exceeded the requested key bound.");
        }

        return ProjectScanPage(page);
    }

    public async ValueTask<RedisKeyValueSnapshot> ReadAsync(
        RedisKeyReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOpen();
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            request.MaximumEntries,
            MaximumEntries);
        var key = Resolve(request.Key);
        using var operation = _lifetime.CreateOperationCancellation(cancellationToken);
        var snapshot = await _redis
            .ReadKeyAsync(key.Reference, request.MaximumEntries, operation.Token)
            .ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        if (snapshot?.Summary?.Key is not { } returnedKey
            || !returnedKey.Bytes.AsSpan().SequenceEqual(key.Reference.Bytes)
            || !string.Equals(
                returnedKey.DisplayName,
                key.Reference.DisplayName,
                StringComparison.Ordinal)
            || snapshot.Entries.Count > request.MaximumEntries)
        {
            throw new InvalidDataException(
                "The Redis provider returned a different key or exceeded the requested entry bound.");
        }

        return ProjectKeySnapshot(snapshot, request);
    }

    public async ValueTask<RedisSearchResult> SearchAsync(
        string index,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        RequireOpen();
        if (!Capabilities.Contains(SessionCapabilities.RedisSearch))
        {
            throw new NotSupportedException("Redis Search is unavailable.");
        }

        RequirePrintable(index, nameof(index), 256);
        RequirePrintable(query, nameof(query), MaximumQueryLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumSearchResults);
        using var operation = _lifetime.CreateOperationCancellation(cancellationToken);
        var result = await _redis
            .SearchAsync(index, query, limit, operation.Token)
            .ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        if (result.Values.Count > limit)
        {
            throw new InvalidDataException(
                "The Redis provider exceeded the requested search bound.");
        }

        return ProjectSearchResult(result, limit);
    }

    public async ValueTask<RedisSearchIndexPage> ListSearchIndexesAsync(
        int maximumIndexes,
        CancellationToken cancellationToken)
    {
        RequireOpen();
        if (!Capabilities.Contains(SessionCapabilities.RedisListIndexes))
        {
            throw new NotSupportedException("Redis Search is unavailable.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumIndexes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumIndexes,
            MaximumSearchIndexes);
        using var operation = _lifetime.CreateOperationCancellation(cancellationToken);
        var indexes = await _redis
            .ListSearchIndexesAsync(operation.Token)
            .ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        return ProjectSearchIndexes(indexes, maximumIndexes);
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(
        CancellationToken cancellationToken) =>
        _lifetime.SnapshotAsync(cancellationToken);

    public IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        CancellationToken cancellationToken) =>
        _lifetime.WatchAsync(afterSequence, cancellationToken);

    public async ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        var outcome = await _lifetime
            .CloseAsync(mode, cancellationToken)
            .ConfigureAwait(false);
        await DisposeRedisOnceAsync().ConfigureAwait(false);
        return outcome;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.DisposeAsync().ConfigureAwait(false);
        await DisposeRedisOnceAsync().ConfigureAwait(false);
    }

    private RedisKeyItem ProjectKey(RedisKeySummary summary)
    {
        ValidateKeySummary(summary);
        var lease = new RedisKeyLease(
            Convert.ToBase64String(summary.Key.Bytes),
            new RedisKeyReference(
                summary.Key.DisplayName,
                [.. summary.Key.Bytes]));
        var reference = new RedisKeyReferenceId(_keys.Lease(lease));
        return new RedisKeyItem(
            reference,
            summary.Key.DisplayName,
            summary.Type,
            summary.TimeToLive,
            summary.MemoryBytes);
    }

    private RedisKeyLease Resolve(RedisKeyReferenceId reference)
    {
        if (!_keys.TryResolve(reference.Value, out var lease) || lease is null)
        {
            throw new KeyNotFoundException(
                "The Redis key reference is unknown or expired.");
        }

        return lease;
    }

    private void RequireOpen()
    {
        if (!_lifetime.IsOpen)
        {
            throw new ObjectDisposedException(nameof(RedisDatabasePanelSession));
        }
    }

    private async ValueTask DisposeRedisOnceAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _redis.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void RequirePrintable(
        string value,
        string parameterName,
        int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A Redis tool argument is invalid.",
                parameterName);
        }
    }

    private sealed class RedisKeyLease : IEquatable<RedisKeyLease>
    {
        public RedisKeyLease(string identity, RedisKeyReference reference)
        {
            Identity = identity;
            Reference = reference;
        }

        public string Identity { get; }

        public RedisKeyReference Reference { get; }

        public bool Equals(RedisKeyLease? other) =>
            other is not null
            && string.Equals(Identity, other.Identity, StringComparison.Ordinal);

        public override bool Equals(object? obj) =>
            obj is RedisKeyLease other && Equals(other);

        public override int GetHashCode() =>
            StringComparer.Ordinal.GetHashCode(Identity);
    }
}
