using System.Collections.Immutable;
using System.Net;

namespace Asura.Files;

public sealed partial class S3FileProvider
{
    public ValueTask<FileProviderResult<FilePage>> ListAsync(
        FileListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(token => ListCoreAsync(request, token), cancellationToken);
    }

    public ValueTask<FileProviderResult<FileEntry>> StatAsync(
        FileStatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(token => StatCoreAsync(request.Location, token), cancellationToken);
    }

    private async ValueTask<FileProviderResult<FilePage>> ListCoreAsync(
        FileListRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PageSize > Capabilities.Limits.MaximumListPageSize)
        {
            return Failure<FilePage>(
                FileProviderErrorCode.LimitExceeded,
                "The requested S3 list page is too large.");
        }

        var prefixResult = ResolvePrefix(request.Location);
        if (!prefixResult.IsSuccess)
        {
            return FileProviderResult<FilePage>.Failure(prefixResult.Error!);
        }

        var prefix = prefixResult.Value!;
        var scope = $"{ProfileId.Value}\n{Authority.Value}\n{prefix.Prefix}";
        string? remoteToken = null;
        var seenTokens = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
        if (request.ContinuationToken is { } continuation)
        {
            if (!_pageCursors.TryGet(continuation, out var cursor) || !string.Equals(cursor!.Scope, scope, StringComparison.Ordinal))
            {
                return Failure<FilePage>(
                    FileProviderErrorCode.InvalidLocation,
                    "The S3 continuation token is invalid for this prefix.");
            }

            remoteToken = cursor.RemoteToken;
            seenTokens = cursor.SeenTokens;
        }

        var page = await _store.ListAsync(
            _options.BucketName,
            prefix.Prefix,
            request.PageSize,
            remoteToken,
            cancellationToken).ConfigureAwait(false);
        if (page.Objects.Count + page.CommonPrefixes.Count > request.PageSize)
        {
            return Failure<FilePage>(
                FileProviderErrorCode.IoFailure,
                "The S3 service returned more entries than requested.");
        }

        var entries = new List<FileEntry>(page.Objects.Count + page.CommonPrefixes.Count);
        foreach (var item in page.Objects)
        {
            if (string.Equals(item.Key, prefix.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var location = CreateListedLocation(prefix, item.Key, isDirectory: false);
            entries.Add(ObjectEntry(location, item.Size, item.LastModifiedAt, item.ETag));
        }

        foreach (var commonPrefix in page.CommonPrefixes)
        {
            var location = CreateListedLocation(prefix, commonPrefix, isDirectory: true);
            entries.Add(PrefixEntry(location, commonPrefix));
        }

        entries.Sort((left, right) => string.CompareOrdinal(ListSortKey(left), ListSortKey(right)));
        FilePageToken? next = null;
        if (page.IsTruncated)
        {
            if (string.IsNullOrEmpty(page.NextContinuationToken))
            {
                return Failure<FilePage>(
                    FileProviderErrorCode.IoFailure,
                    "The S3 service truncated a list response without a continuation token.");
            }

            if (seenTokens.Contains(page.NextContinuationToken))
            {
                return Failure<FilePage>(FileProviderErrorCode.IoFailure,
                    "The S3 service repeated a continuation token while listing this prefix.");
            }

            next = _pageCursors.Add(new S3PageCursor(scope, page.NextContinuationToken,
                seenTokens.Add(page.NextContinuationToken)));
        }

        return FileProviderResult<FilePage>.Success(new FilePage(entries, next));
    }

    private async ValueTask<FileProviderResult<FileEntry>> StatCoreAsync(
        FileLocation location,
        CancellationToken cancellationToken)
    {
        var identityError = ValidateIdentity(location);
        if (identityError is not null)
        {
            return FileProviderResult<FileEntry>.Failure(identityError);
        }

        if (location.IsContainerRoot
            || location.Address is FileLocationAddress.Hierarchical { Path.IsRoot: true })
        {
            var root = ResolvePrefix(location);
            return root.IsSuccess
                ? FileProviderResult<FileEntry>.Success(PrefixEntry(location, string.Empty))
                : FileProviderResult<FileEntry>.Failure(root.Error!);
        }

        var prefixCandidate = TryResolveExplicitPrefix(location);
        if (prefixCandidate is not null)
        {
            return await StatPrefixAsync(prefixCandidate, cancellationToken).ConfigureAwait(false);
        }

        var resolved = ResolveObject(location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(resolved.Error!);
        }

        try
        {
            var expectedEtag = location.Version?.Value;
            var metadata = await _store.HeadAsync(
                _options.BucketName,
                resolved.Value!.Key,
                expectedEtag,
                cancellationToken).ConfigureAwait(false);
            var entry = ObjectEntry(location, metadata);
            if (location.Version is { } expected && entry.Version != expected)
            {
                return Failure<FileEntry>(
                    FileProviderErrorCode.PreconditionFailed,
                    "The requested S3 object version is stale.");
            }

            return FileProviderResult<FileEntry>.Success(entry);
        }
        catch (S3StoreException exception)
            when (exception.StatusCode == HttpStatusCode.NotFound
                  && location.Address is FileLocationAddress.Hierarchical)
        {
            var inferredPrefix = ResolvePrefix(location.WithVersion(null));
            return inferredPrefix.IsSuccess
                ? await StatPrefixAsync(inferredPrefix.Value!, cancellationToken).ConfigureAwait(false)
                : FileProviderResult<FileEntry>.Failure(inferredPrefix.Error!);
        }
    }

    private ResolvedS3Prefix? TryResolveExplicitPrefix(FileLocation location)
    {
        if (location.Version is null)
        {
            return null;
        }

        var candidate = ResolvePrefix(location);
        return candidate.IsSuccess ? candidate.Value : null;
    }

    private async ValueTask<FileProviderResult<FileEntry>> StatPrefixAsync(
        ResolvedS3Prefix prefix,
        CancellationToken cancellationToken)
    {
        var page = await _store.ListAsync(
            _options.BucketName,
            prefix.Prefix,
            maximumItems: 1,
            continuationToken: null,
            cancellationToken).ConfigureAwait(false);
        if (page.Objects.Count == 0 && page.CommonPrefixes.Count == 0)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.NotFound,
                "The S3 prefix does not contain any objects.");
        }

        return FileProviderResult<FileEntry>.Success(PrefixEntry(prefix.Location, prefix.Prefix));
    }

    private FileLocation CreateListedLocation(
        ResolvedS3Prefix parent,
        string key,
        bool isDirectory)
    {
        var relative = key.StartsWith(parent.Prefix, StringComparison.Ordinal)
            ? key[parent.Prefix.Length..]
            : key;
        if (isDirectory && relative.EndsWith('/'))
        {
            relative = relative[..^1];
        }

        if (parent.HierarchicalPath is { } parentPath
            && !relative.Contains("/", StringComparison.Ordinal)
            && TryCreateSegment(relative, out var segment))
        {
            return new FileLocation(ProfileId, Authority, parentPath.Append(segment));
        }

        return FileLocation.ForObjectKey(
            ProfileId,
            Authority,
            new FileObjectKey(key));
    }

    private static bool TryCreateSegment(string value, out FilePathSegment segment)
    {
        try
        {
            segment = new FilePathSegment(value);
            return true;
        }
        catch (ArgumentException)
        {
            segment = default;
            return false;
        }
    }

    private static string ListSortKey(FileEntry entry) => entry.Location.Address switch
    {
        FileLocationAddress.Hierarchical value => value.Path.Name?.Value ?? string.Empty,
        FileLocationAddress.Object value => value.Key.Value,
        _ => string.Empty,
    };
}
