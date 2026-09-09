using System.Net;

namespace Asura.Files.Tests;

internal sealed class FakeS3ObjectStore : IS3ObjectStore
{
    private readonly Dictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);
    private long _revision;

    public int CopyCalls { get; private set; }

    public int ReadCalls { get; private set; }

    public string? LastContinuationToken { get; private set; }

    public int ContinuationTokenPadding { get; init; }

    public Func<string?, S3ObjectPage>? ListingOverride { get; init; }

    public ValueTask<S3ObjectPage> ListAsync(
        string bucket,
        string prefix,
        int maximumItems,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastContinuationToken = continuationToken;
        if (ListingOverride is not null)
        {
            return ValueTask.FromResult(ListingOverride(continuationToken));
        }
        var offset = DecodeOffset(continuationToken);
        var rows = new SortedDictionary<string, ListRow>(StringComparer.Ordinal);
        foreach (var pair in _objects.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var remainder = pair.Key[prefix.Length..];
            var separator = remainder.IndexOf('/');
            if (separator >= 0)
            {
                var commonPrefix = prefix + remainder[..(separator + 1)];
                rows.TryAdd(commonPrefix, new ListRow(commonPrefix));
            }
            else
            {
                rows[pair.Key] = new ListRow(pair.Key, pair.Value);
            }
        }

        var pageRows = rows.Values.Skip(offset).Take(maximumItems).ToArray();
        var nextOffset = offset + pageRows.Length;
        var truncated = nextOffset < rows.Count;
        return ValueTask.FromResult(new S3ObjectPage(
            [.. pageRows
                .Where(row => row.Object is not null)
                .Select(row => new S3ObjectItem(
                    row.Key,
                    row.Object!.Content.LongLength,
                    row.Object.LastModifiedAt,
                    row.Object.ETag))],
            [.. pageRows.Where(row => row.Object is null).Select(row => row.Key)],
            truncated,
            truncated ? EncodeOffset(nextOffset) : null));
    }

    public ValueTask<S3ObjectMetadata> HeadAsync(
        string bucket,
        string key,
        string? etagToMatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = Get(key);
        MatchEtag(value, etagToMatch);
        return ValueTask.FromResult(new S3ObjectMetadata(
            value.Content.LongLength,
            value.LastModifiedAt,
            value.ETag));
    }

    public ValueTask<S3ObjectRead> ReadAsync(
        string bucket,
        string key,
        long start,
        long endInclusive,
        string etagToMatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCalls++;
        var value = Get(key);
        MatchEtag(value, etagToMatch);
        if (start < 0 || endInclusive < start || start >= value.Content.LongLength)
        {
            throw Error(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange");
        }

        var count = checked((int)Math.Min(endInclusive - start + 1, value.Content.LongLength - start));
        var stream = new MemoryStream(value.Content, checked((int)start), count, writable: false);
        return ValueTask.FromResult(new S3ObjectRead(
            stream,
            count,
            value.ETag,
            new StreamOwner(stream)));
    }

    public async ValueTask<S3ObjectMutation> WriteAsync(
        string bucket,
        string key,
        Stream source,
        long contentLength,
        string? ifMatch,
        string? ifNoneMatch,
        CancellationToken cancellationToken)
    {
        CheckDestination(key, ifMatch, ifNoneMatch);
        await using var destination = new MemoryStream();
        await source.CopyToAsync(destination, cancellationToken);
        if (destination.Length != contentLength)
        {
            throw new EndOfStreamException("The fake S3 upload did not receive its declared content.");
        }

        var value = NewObject(destination.ToArray());
        _objects[key] = value;
        return new S3ObjectMutation(value.ETag, value.LastModifiedAt);
    }

    public ValueTask<S3ObjectMutation> CopyAsync(
        string bucket,
        string sourceKey,
        string destinationKey,
        string sourceEtagToMatch,
        string? destinationIfMatch,
        string? destinationIfNoneMatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CopyCalls++;
        var source = Get(sourceKey);
        MatchEtag(source, sourceEtagToMatch);
        CheckDestination(destinationKey, destinationIfMatch, destinationIfNoneMatch);
        var value = NewObject([.. source.Content]);
        _objects[destinationKey] = value;
        return ValueTask.FromResult(new S3ObjectMutation(value.ETag, value.LastModifiedAt));
    }

    public ValueTask DeleteAsync(
        string bucket,
        string key,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = Get(key);
        MatchEtag(value, ifMatch);
        _objects.Remove(key);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The bucket's own ACL store, keyed the way the service keys it. The owner
    /// is fixed because a fake that let it change would be modelling the one
    /// thing PutObjectAcl must never be allowed to do by accident.
    /// </summary>
    public Dictionary<string, S3ObjectAcl> Acls { get; } = new(StringComparer.Ordinal);

    public ValueTask<S3ObjectAcl> GetAclAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Get(key);
        return ValueTask.FromResult(Acls.TryGetValue(key, out var acl)
            ? acl
            : new S3ObjectAcl(
                "owner-canonical-id",
                "owner",
                [new S3ObjectGrant("CanonicalUser", "owner-canonical-id", "owner", null, "FULL_CONTROL")]));
    }

    public ValueTask<S3ObjectAcl> PutAclAsync(
        string bucket,
        string key,
        S3ObjectAcl acl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acl);
        cancellationToken.ThrowIfCancellationRequested();
        _ = Get(key);
        Acls[key] = acl;
        return ValueTask.FromResult(acl);
    }

    public bool Contains(string key) => _objects.ContainsKey(key);

    private StoredObject Get(string key) => _objects.TryGetValue(key, out var value)
        ? value
        : throw Error(HttpStatusCode.NotFound, "NoSuchKey");

    private void CheckDestination(string key, string? ifMatch, string? ifNoneMatch)
    {
        _objects.TryGetValue(key, out var existing);
        if (string.Equals(ifNoneMatch, "*", StringComparison.Ordinal) && existing is not null)
        {
            throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        }

        if (string.Equals(ifMatch, "*", StringComparison.Ordinal) && existing is null)
        {
            throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        }

        if (ifMatch is not null and not "*" && !string.Equals(existing?.ETag, ifMatch, StringComparison.Ordinal))
        {
            throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        }
    }

    private static void MatchEtag(StoredObject value, string? etag)
    {
        if (etag is not null and not "*" && !string.Equals(value.ETag, etag, StringComparison.Ordinal))
        {
            throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        }
    }

    private StoredObject NewObject(byte[] content)
    {
        var revision = Interlocked.Increment(ref _revision);
        return new StoredObject(
            content,
            $"\"fake-{revision}\"",
            DateTimeOffset.UnixEpoch.AddTicks(revision));
    }

    private int DecodeOffset(string? token)
    {
        if (token is null)
        {
            return 0;
        }

        var separator = token.IndexOf(':');
        return int.Parse(token[..separator], System.Globalization.CultureInfo.InvariantCulture);
    }

    private string EncodeOffset(int offset) =>
        $"{offset}:{new string('x', ContinuationTokenPadding)}";

    private static S3StoreException Error(HttpStatusCode status, string code) =>
        new(status, code, code, new HttpRequestException(code));

    private sealed record StoredObject(byte[] Content, string ETag, DateTimeOffset LastModifiedAt);

    private sealed record ListRow(string Key, StoredObject? Object = null);

    private sealed class StreamOwner(Stream stream) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await stream.DisposeAsync();
    }
}
