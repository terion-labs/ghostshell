using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Asura.Application;

namespace Asura.Files;

/// <summary>
/// Exposes one S3 or S3-compatible bucket. Hierarchical locations provide delimiter-based
/// navigation, while object-key locations preserve every legal key byte-for-byte.
/// </summary>
public sealed partial class S3FileProvider : IFileProvider
{
    private const long MaximumReadBytes = 64L * 1024 * 1024;
    private const int MaximumBufferSize = 1024 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly S3FileProviderOptions _options;
    private readonly IS3ObjectStore _store;
    private readonly FilePageCursorStore<S3PageCursor> _pageCursors = new();

    public S3FileProvider(IAmazonS3 client, S3FileProviderOptions options)
        : this(new AwsS3ObjectStore(client ?? throw new ArgumentNullException(nameof(client))), options)
    {
    }

    internal S3FileProvider(IS3ObjectStore store, S3FileProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _options = options;
        ProfileId = options.ProfileId;
        Authority = options.Authority;
        Capabilities = DefaultCapabilities;
    }

    internal static FileProviderCapabilities DefaultCapabilities { get; } =
        new(
            FileProviderCapability.List
            | FileProviderCapability.Stat
            | FileProviderCapability.RangedRead
            | FileProviderCapability.StreamingWrite
            | FileProviderCapability.Copy
            | FileProviderCapability.Delete
            | FileProviderCapability.ServerSideCopy
            | FileProviderCapability.AccessControlLists
            | FileProviderCapability.Pagination,
            FileNameComparison.CaseSensitive,
            new FileProviderLimits(
                maximumListPageSize: 1_000,
                maximumReadBytes: MaximumReadBytes,
                maximumBufferSize: MaximumBufferSize));

    public FileProviderProfileId ProfileId { get; }

    public FileAuthority Authority { get; }

    public FileProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Who the bucket says can reach one object. There is no mode here: an
    /// object store's answer is a list of accounts and well-known groups, and
    /// squeezing that into owner-group-other would lose most of it.
    /// </summary>
    public ValueTask<FileProviderResult<FileAccessControl>> GetAccessControlAsync(
        FileAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            async token =>
            {
                var resolved = ResolveObject(request.Location);
                if (!resolved.IsSuccess)
                {
                    return FileProviderResult<FileAccessControl>.Failure(resolved.Error!);
                }

                var acl = await _store.GetAclAsync(
                    _options.BucketName,
                    resolved.Value!.Key,
                    token).ConfigureAwait(false);
                return FileProviderResult<FileAccessControl>.Success(ToAccessControl(acl));
            },
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileAccessControl>> SetAccessControlAsync(
        FileSetAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            async token =>
            {
                if (request.Grants is not { } grants)
                {
                    return Failure<FileAccessControl>(
                        FileProviderErrorCode.UnsupportedCapability,
                        "An object store is described by who is granted what, "
                        + "not by permission bits.");
                }

                var resolved = ResolveObject(request.Location);
                if (!resolved.IsSuccess)
                {
                    return FileProviderResult<FileAccessControl>.Failure(resolved.Error!);
                }

                // Read first for the owner: an ACL sent without one is the
                // service's way of being told the object changes hands.
                var current = await _store.GetAclAsync(
                    _options.BucketName,
                    resolved.Value!.Key,
                    token).ConfigureAwait(false);
                var written = await _store.PutAclAsync(
                    _options.BucketName,
                    resolved.Value.Key,
                    new S3ObjectAcl(
                        current.OwnerId,
                        current.OwnerDisplayName,
                        [.. grants.SelectMany(ToStoreGrants)]),
                    token).ConfigureAwait(false);
                return FileProviderResult<FileAccessControl>.Success(ToAccessControl(written));
            },
            cancellationToken);
    }

    /// <summary>
    /// The service repeats a grantee once per permission; a reader wants one
    /// row per party with everything it holds, so they are folded back together.
    /// </summary>
    private static FileAccessControl ToAccessControl(S3ObjectAcl acl)
    {
        var folded = new Dictionary<string, (FilePanelGrantee Grantee, FilePanelAccessRight Rights)>(
            StringComparer.Ordinal);
        foreach (var grant in acl.Grants)
        {
            var grantee = ToGrantee(grant, acl.OwnerId);
            var key = $"{grantee.Kind}:{grantee.Id}";
            var rights = ToRight(grant.Permission);
            folded[key] = folded.TryGetValue(key, out var existing)
                ? (existing.Grantee, existing.Rights | rights)
                : (grantee, rights);
        }

        return new FileAccessControl(
            owner: acl.OwnerDisplayName ?? acl.OwnerId,
            grants: [.. folded.Values.Select(entry => new FilePanelAccessGrant(entry.Grantee, entry.Rights))]);
    }

    private static FilePanelGrantee ToGrantee(S3ObjectGrant grant, string? ownerId) =>
        grant.GranteeUri switch
        {
            "http://acs.amazonaws.com/groups/global/AllUsers" =>
                new FilePanelGrantee(FilePanelGranteeKind.Everyone),
            "http://acs.amazonaws.com/groups/global/AuthenticatedUsers" =>
                new FilePanelGrantee(FilePanelGranteeKind.AuthenticatedUsers),
            "http://acs.amazonaws.com/groups/s3/LogDelivery" =>
                new FilePanelGrantee(FilePanelGranteeKind.LogDelivery),
            _ when grant.GranteeId is { } id && string.Equals(id, ownerId, StringComparison.Ordinal) =>
                new FilePanelGrantee(FilePanelGranteeKind.Owner, id, grant.GranteeDisplayName),
            _ => new FilePanelGrantee(
                FilePanelGranteeKind.User,
                grant.GranteeId ?? grant.GranteeUri ?? "unknown",
                grant.GranteeDisplayName),
        };

    private static FilePanelAccessRight ToRight(string permission) => permission switch
    {
        "FULL_CONTROL" => FilePanelAccessRight.FullControl,
        "READ" => FilePanelAccessRight.Read,
        "WRITE" => FilePanelAccessRight.Write,
        "READ_ACP" => FilePanelAccessRight.ReadAcl,
        "WRITE_ACP" => FilePanelAccessRight.WriteAcl,
        _ => FilePanelAccessRight.None,
    };

    private static IEnumerable<S3ObjectGrant> ToStoreGrants(FilePanelAccessGrant grant)
    {
        var (type, id, uri) = grant.Grantee.Kind switch
        {
            FilePanelGranteeKind.Everyone => (
                "Group",
                (string?)null,
                "http://acs.amazonaws.com/groups/global/AllUsers"),
            FilePanelGranteeKind.AuthenticatedUsers => (
                "Group",
                null,
                "http://acs.amazonaws.com/groups/global/AuthenticatedUsers"),
            FilePanelGranteeKind.LogDelivery => (
                "Group",
                null,
                "http://acs.amazonaws.com/groups/s3/LogDelivery"),
            _ => ("CanonicalUser", grant.Grantee.Id, (string?)null),
        };

        // Back out to one row per permission, which is the shape the service
        // stores and the shape it expects to be handed.
        if (grant.Rights == FilePanelAccessRight.FullControl)
        {
            yield return new S3ObjectGrant(
                type,
                id,
                grant.Grantee.DisplayName,
                uri,
                "FULL_CONTROL");
            yield break;
        }

        foreach (var (right, permission) in new[]
                 {
                     (FilePanelAccessRight.Read, "READ"),
                     (FilePanelAccessRight.Write, "WRITE"),
                     (FilePanelAccessRight.ReadAcl, "READ_ACP"),
                     (FilePanelAccessRight.WriteAcl, "WRITE_ACP"),
                 })
        {
            if (grant.Rights.HasFlag(right))
            {
                yield return new S3ObjectGrant(
                    type,
                    id,
                    grant.Grantee.DisplayName,
                    uri,
                    permission);
            }
        }
    }

    private FileProviderResult<ResolvedS3Object> ResolveObject(FileLocation location)
    {
        var identityError = ValidateIdentity(location);
        if (identityError is not null)
        {
            return FileProviderResult<ResolvedS3Object>.Failure(identityError);
        }

        string? key = location.Address switch
        {
            FileLocationAddress.Object value => value.Key.Value,
            FileLocationAddress.Hierarchical { Path.IsRoot: false } value =>
                string.Join('/', value.Path.Segments.Select(segment => segment.Value)),
            _ => null,
        };
        if (key is null)
        {
            return Failure<ResolvedS3Object>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The S3 bucket root is not an object.");
        }

        var keyError = ValidateObjectKey(key);
        return keyError is null
            ? FileProviderResult<ResolvedS3Object>.Success(new ResolvedS3Object(location, key))
            : FileProviderResult<ResolvedS3Object>.Failure(keyError);
    }

    private FileProviderResult<ResolvedS3Prefix> ResolvePrefix(FileLocation location)
    {
        var identityError = ValidateIdentity(location);
        if (identityError is not null)
        {
            return FileProviderResult<ResolvedS3Prefix>.Failure(identityError);
        }

        string prefix;
        FilePath? hierarchicalPath;
        switch (location.Address)
        {
            case FileLocationAddress.ContainerRoot:
                prefix = string.Empty;
                hierarchicalPath = FilePath.Root;
                break;
            case FileLocationAddress.Hierarchical hierarchical:
                prefix = hierarchical.Path.IsRoot
                    ? string.Empty
                    : $"{string.Join('/', hierarchical.Path.Segments.Select(segment => segment.Value))}/";
                hierarchicalPath = hierarchical.Path;
                break;
            case FileLocationAddress.Object value when IsPrefixVersion(location.Version, value.Key.Value):
                prefix = value.Key.Value;
                hierarchicalPath = null;
                break;
            default:
                return Failure<ResolvedS3Prefix>(
                    FileProviderErrorCode.NotDirectory,
                    "An exact S3 object key is not a navigable prefix.");
        }

        var prefixError = ValidatePrefix(prefix);
        if (prefixError is not null)
        {
            return FileProviderResult<ResolvedS3Prefix>.Failure(prefixError);
        }

        var version = PrefixVersion(prefix);
        if (location.Version is { } expected && expected != version)
        {
            return Failure<ResolvedS3Prefix>(
                FileProviderErrorCode.PreconditionFailed,
                "The S3 prefix location version is invalid.");
        }

        return FileProviderResult<ResolvedS3Prefix>.Success(
            new ResolvedS3Prefix(location, prefix, hierarchicalPath, version));
    }

    private FileProviderError? ValidateIdentity(FileLocation location) =>
        location.ProviderProfileId == ProfileId && location.Authority == Authority
            ? null
            : FileProviderError.Create(
                FileProviderErrorCode.InvalidLocation,
                "The location belongs to another S3 profile or bucket authority.");

    private static FileProviderError? ValidateObjectKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return FileProviderError.Create(
                FileProviderErrorCode.InvalidLocation,
                "An S3 object key must contain between 1 and 1,024 UTF-8 bytes.");
        }

        try
        {
            if (StrictUtf8.GetByteCount(key) > 1_024)
            {
                return FileProviderError.Create(
                    FileProviderErrorCode.InvalidLocation,
                    "An S3 object key must contain between 1 and 1,024 UTF-8 bytes.");
            }
        }
        catch (EncoderFallbackException)
        {
            return FileProviderError.Create(
                FileProviderErrorCode.InvalidName,
                "An S3 object key must be valid Unicode text with an exact UTF-8 representation.");
        }

        return null;
    }

    private static FileProviderError? ValidatePrefix(string prefix) =>
        prefix.Length == 0 ? null : ValidateObjectKey(prefix);

    private FileEntry ObjectEntry(FileLocation location, S3ObjectMetadata metadata) =>
        ObjectEntry(location, metadata.Size, metadata.LastModifiedAt, metadata.ETag);

    private FileEntry ObjectEntry(
        FileLocation location,
        long size,
        DateTimeOffset? lastModifiedAt,
        string etag)
    {
        var versionResult = ParseEtag(etag);
        if (!versionResult.IsSuccess)
        {
            throw new InvalidDataException(versionResult.Error!.Message);
        }

        var version = versionResult.Value;
        return new FileEntry(
            location.WithVersion(version),
            FileEntryKind.File,
            size,
            lastModifiedAt,
            version,
            IsHiddenObject(location));
    }

    private FileEntry PrefixEntry(FileLocation location, string prefix)
    {
        var version = PrefixVersion(prefix);
        return new FileEntry(
            location.WithVersion(version),
            FileEntryKind.Directory,
            Size: null,
            LastModifiedAt: null,
            version,
            IsHiddenKey(prefix.TrimEnd('/')));
    }

    private static FileProviderResult<FileVersion> ParseEtag(string? etag)
    {
        try
        {
            return FileProviderResult<FileVersion>.Success(new FileVersion(etag!));
        }
        catch (ArgumentException)
        {
            return Failure<FileVersion>(
                FileProviderErrorCode.IoFailure,
                "The S3 service returned an invalid or missing ETag.");
        }
    }

    private static FileVersion PrefixVersion(string prefix)
    {
        var digest = SHA256.HashData(StrictUtf8.GetBytes(prefix));
        return new FileVersion($"s3-prefix:{Convert.ToHexString(digest)}");
    }

    private static bool IsPrefixVersion(FileVersion? version, string prefix) =>
        version == PrefixVersion(prefix);

    private static bool IsHiddenObject(FileLocation location) => location.Address switch
    {
        FileLocationAddress.Hierarchical value =>
            value.Path.Name is { } name && name.Value.StartsWith('.'),
        FileLocationAddress.Object value => IsHiddenKey(value.Key.Value),
        _ => false,
    };

    private static bool IsHiddenKey(string key)
    {
        var separator = key.LastIndexOf('/');
        var name = separator >= 0 ? key[(separator + 1)..] : key;
        return name.StartsWith('.');
    }

    private static FileProviderResult<T> Failure<T>(
        FileProviderErrorCode code,
        string message,
        bool retryable = false) =>
        RemoteFileProviderUtilities.Failure<T>(code, message, retryable);

    private async ValueTask<FileProviderResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<FileProviderResult<T>>> operation,
        CancellationToken cancellationToken,
        FileMutationPrecondition? mutationPrecondition = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<T>(FileProviderErrorCode.Cancelled, "The S3 operation was cancelled.");
        }
        catch (OperationCanceledException exception)
        {
            return Failure<T>(FileProviderErrorCode.IoFailure, exception.Message, retryable: true);
        }
        catch (S3StoreException exception)
        {
            return FileProviderResult<T>.Failure(MapStoreError(exception, mutationPrecondition));
        }
        catch (EndOfStreamException exception)
        {
            return Failure<T>(FileProviderErrorCode.UnexpectedEndOfStream, exception.Message);
        }
        catch (HttpRequestException exception)
        {
            return Failure<T>(FileProviderErrorCode.IoFailure, exception.Message, retryable: true);
        }
        catch (IOException exception)
        {
            return Failure<T>(FileProviderErrorCode.IoFailure, exception.Message, retryable: true);
        }
    }

    private static FileProviderError MapStoreError(
        S3StoreException exception,
        FileMutationPrecondition? mutationPrecondition)
    {
        if (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            var code = mutationPrecondition is FileMutationPrecondition.MustNotExist
                ? FileProviderErrorCode.Conflict
                : FileProviderErrorCode.PreconditionFailed;
            return FileProviderError.Create(code, exception.Message);
        }

        if (exception.ServiceCode is "EntityTooLarge" or "InvalidRequest")
        {
            return FileProviderError.Create(FileProviderErrorCode.LimitExceeded, exception.Message);
        }

        return exception.StatusCode switch
        {
            HttpStatusCode.BadRequest => FileProviderError.Create(
                FileProviderErrorCode.InvalidLocation,
                exception.Message),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FileProviderError.Create(
                FileProviderErrorCode.AccessDenied,
                exception.Message),
            HttpStatusCode.NotFound => FileProviderError.Create(
                FileProviderErrorCode.NotFound,
                exception.Message),
            HttpStatusCode.Conflict => FileProviderError.Create(
                FileProviderErrorCode.Conflict,
                exception.Message,
                retryable: true),
            HttpStatusCode.RequestedRangeNotSatisfiable => FileProviderError.Create(
                FileProviderErrorCode.RangeNotSatisfiable,
                exception.Message),
            HttpStatusCode.TooManyRequests => FileProviderError.Create(
                FileProviderErrorCode.IoFailure,
                exception.Message,
                retryable: true),
            >= HttpStatusCode.InternalServerError => FileProviderError.Create(
                FileProviderErrorCode.IoFailure,
                exception.Message,
                retryable: true),
            _ => FileProviderError.Create(FileProviderErrorCode.IoFailure, exception.Message),
        };
    }

    private sealed record ResolvedS3Object(FileLocation Location, string Key);

    private sealed record ResolvedS3Prefix(
        FileLocation Location,
        string Prefix,
        FilePath? HierarchicalPath,
        FileVersion Version);

    private sealed record S3PageCursor(string Scope, string RemoteToken,
        System.Collections.Immutable.ImmutableHashSet<string> SeenTokens);
}
