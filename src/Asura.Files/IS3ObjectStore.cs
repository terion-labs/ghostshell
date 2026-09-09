namespace Asura.Files;

/// <summary>
/// Narrow seam over the AWS SDK. It keeps SDK transport types out of provider semantics and lets
/// deterministic tests model S3 responses without credentials or a live endpoint.
/// </summary>
internal interface IS3ObjectStore
{
    ValueTask<S3ObjectPage> ListAsync(
        string bucket,
        string prefix,
        int maximumItems,
        string? continuationToken,
        CancellationToken cancellationToken);

    ValueTask<S3ObjectMetadata> HeadAsync(
        string bucket,
        string key,
        string? etagToMatch,
        CancellationToken cancellationToken);

    ValueTask<S3ObjectRead> ReadAsync(
        string bucket,
        string key,
        long start,
        long endInclusive,
        string etagToMatch,
        CancellationToken cancellationToken);

    ValueTask<S3ObjectMutation> WriteAsync(
        string bucket,
        string key,
        Stream source,
        long contentLength,
        string? ifMatch,
        string? ifNoneMatch,
        CancellationToken cancellationToken);

    ValueTask<S3ObjectMutation> CopyAsync(
        string bucket,
        string sourceKey,
        string destinationKey,
        string sourceEtagToMatch,
        string? destinationIfMatch,
        string? destinationIfNoneMatch,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(
        string bucket,
        string key,
        string? ifMatch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Who the bucket says can read or change one object. S3 answers with a
    /// list of grants rather than a mode: there is no owner-group-other here,
    /// only named accounts and a few well-known groups.
    /// </summary>
    ValueTask<S3ObjectAcl> GetAclAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces that list wholesale, which is what the service offers: there is
    /// no adding one grant. The owner goes back exactly as it came, because
    /// leaving it out transfers the object.
    /// </summary>
    ValueTask<S3ObjectAcl> PutAclAsync(
        string bucket,
        string key,
        S3ObjectAcl acl,
        CancellationToken cancellationToken);
}

internal sealed record S3ObjectAcl(
    string? OwnerId,
    string? OwnerDisplayName,
    IReadOnlyList<S3ObjectGrant> Grants);

/// <summary>
/// One row of an S3 ACL, in the service's own words: a grantee type, an id, and
/// one permission. The service repeats a grantee once per permission, which is
/// why this is a grant rather than a set of them.
/// </summary>
internal sealed record S3ObjectGrant(
    string GranteeType,
    string? GranteeId,
    string? GranteeDisplayName,
    string? GranteeUri,
    string Permission);

internal sealed record S3ObjectPage(
    IReadOnlyList<S3ObjectItem> Objects,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextContinuationToken);

internal sealed record S3ObjectItem(
    string Key,
    long Size,
    DateTimeOffset? LastModifiedAt,
    string ETag);

internal sealed record S3ObjectMetadata(
    long Size,
    DateTimeOffset? LastModifiedAt,
    string ETag);

internal sealed record S3ObjectMutation(
    string ETag,
    DateTimeOffset? LastModifiedAt);

internal sealed class S3ObjectRead(
    Stream content,
    long contentLength,
    string etag,
    IAsyncDisposable owner) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public long ContentLength { get; } = contentLength;

    public string ETag { get; } = etag;

    public ValueTask DisposeAsync() => owner.DisposeAsync();
}
