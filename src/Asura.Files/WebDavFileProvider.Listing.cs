using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Asura.Files;

public sealed partial class WebDavFileProvider
{
    private static readonly XNamespace DavNamespace = "DAV:";
    private const string PropertyRequestBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:">
          <d:prop>
            <d:resourcetype />
            <d:getcontentlength />
            <d:getlastmodified />
            <d:getetag />
          </d:prop>
        </d:propfind>
        """;

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
                "The requested WebDAV list page is too large.");
        }

        var resolved = ResolveLocation(request.Location, appendDirectorySlash: true);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FilePage>.Failure(resolved.Error!);
        }

        var scope = resolved.Value!.Uri.AbsoluteUri;
        if (request.ContinuationToken is { } continuation)
        {
            if (!_pageCursors.TryGet(continuation, out var cursor) || !string.Equals(cursor!.Scope, scope, StringComparison.Ordinal))
            {
                return Failure<FilePage>(
                    FileProviderErrorCode.InvalidLocation,
                    "The WebDAV continuation token is invalid for this collection.");
            }

            return FileProviderResult<FilePage>.Success(PageFromSnapshot(
                cursor.Entries,
                cursor.Offset,
                request.PageSize,
                scope));
        }

        var resources = await ReadPropertiesAsync(
            resolved.Value,
            depth: 1,
            cancellationToken).ConfigureAwait(false);
        if (!resources.IsSuccess)
        {
            return FileProviderResult<FilePage>.Failure(resources.Error!);
        }

        var resourceEntries = resources.Value!;
        var resolvedLocation = resolved.Value!;
        var self = resourceEntries.FirstOrDefault(entry => EntryPath(entry).Equals(resolvedLocation.Path));
        if (self is null)
        {
            return Failure<FilePage>(
                FileProviderErrorCode.NotFound,
                "The WebDAV collection did not describe itself.");
        }

        if (self.Kind != FileEntryKind.Directory)
        {
            return Failure<FilePage>(
                FileProviderErrorCode.NotDirectory,
                "The WebDAV location is not a collection.");
        }

        if (request.Location.Version is { } expected && self.Version != expected)
        {
            return Failure<FilePage>(
                FileProviderErrorCode.PreconditionFailed,
                "The requested WebDAV collection version is stale.");
        }

        var children = resourceEntries
            .Where(entry => !ReferenceEquals(entry, self))
            .Where(entry => EntryPath(entry).Parent.Equals(resolvedLocation.Path))
            .OrderBy(entry => EntryPath(entry).Name!.Value.Value, StringComparer.Ordinal)
            .ToArray();
        if (children.Length > MaximumListedEntries)
        {
            return Failure<FilePage>(
                FileProviderErrorCode.LimitExceeded,
                $"The WebDAV collection contains more than {MaximumListedEntries} bounded entries.");
        }

        return FileProviderResult<FilePage>.Success(
            PageFromSnapshot(children, offset: 0, request.PageSize, scope));
    }

    private async ValueTask<FileProviderResult<FileEntry>> StatCoreAsync(
        FileLocation location,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveLocation(location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(resolved.Error!);
        }

        var resources = await ReadPropertiesAsync(
            resolved.Value!,
            depth: 0,
            cancellationToken).ConfigureAwait(false);
        if (!resources.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(resources.Error!);
        }

        var resolvedLocation = resolved.Value!;
        var entry = resources.Value!.FirstOrDefault(value => EntryPath(value).Equals(resolvedLocation.Path));
        if (entry is null)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.NotFound,
                "The WebDAV resource was not found in its property response.");
        }

        if (location.Version is { } expected && entry.Version != expected)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.PreconditionFailed,
                "The requested WebDAV resource version is stale.");
        }

        return FileProviderResult<FileEntry>.Success(entry with
        {
            Location = location.WithVersion(entry.Version),
        });
    }

    private FilePage PageFromSnapshot(
        IReadOnlyList<FileEntry> entries,
        int offset,
        int pageSize,
        string scope)
    {
        var pageItems = entries.Skip(offset).Take(pageSize).ToArray();
        var nextOffset = offset + pageItems.Length;
        FilePageToken? next = nextOffset < entries.Count
            ? _pageCursors.Add(new WebDavPageCursor(scope, entries, nextOffset))
            : null;
        return new FilePage(pageItems, next);
    }

    private async ValueTask<FileProviderResult<IReadOnlyList<FileEntry>>> ReadPropertiesAsync(
        ResolvedWebDavLocation target,
        int depth,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(PropFindMethod, target.Uri)
        {
            Content = new StringContent(PropertyRequestBody, Encoding.UTF8, "application/xml"),
        };
        request.Headers.TryAddWithoutValidation("Depth", depth.ToString(CultureInfo.InvariantCulture));
        AddNoCacheHeaders(request);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var responseScopeError = ValidateResponseScope(response);
        if (responseScopeError is not null)
        {
            return FileProviderResult<IReadOnlyList<FileEntry>>.Failure(responseScopeError);
        }

        if ((int)response.StatusCode != 207)
        {
            return HttpFailure<IReadOnlyList<FileEntry>>(response);
        }

        var body = await ReadBoundedBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return FileProviderResult<IReadOnlyList<FileEntry>>.Failure(body.Error!);
        }

        var document = ParsePropertyDocument(body.Value!);
        var entries = new List<FileEntry>();
        foreach (var responseElement in document.Root?.Elements(DavNamespace + "response") ?? [])
        {
            var entry = ParsePropertyEntry(responseElement, target.Uri);
            if (!entry.IsSuccess)
            {
                return FileProviderResult<IReadOnlyList<FileEntry>>.Failure(entry.Error!);
            }

            if (entry.Value!.Entry is { } parsedEntry)
            {
                entries.Add(parsedEntry);
                if (entries.Count > MaximumListedEntries + 1)
                {
                    return Failure<IReadOnlyList<FileEntry>>(
                        FileProviderErrorCode.LimitExceeded,
                        "The WebDAV property response contains too many entries.");
                }
            }
        }

        return FileProviderResult<IReadOnlyList<FileEntry>>.Success(entries);
    }

    private static XDocument ParsePropertyDocument(byte[] body)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private FileProviderResult<ParsedPropertyEntry> ParsePropertyEntry(
        XElement responseElement,
        Uri requestUri)
    {
        var href = responseElement.Element(DavNamespace + "href")?.Value;
        if (string.IsNullOrWhiteSpace(href))
        {
            return Failure<ParsedPropertyEntry>(
                FileProviderErrorCode.IoFailure,
                "A WebDAV property response omitted its href.");
        }

        var successfulProperty = responseElement
            .Elements(DavNamespace + "propstat")
            .FirstOrDefault(IsSuccessfulPropertyStatus)
            ?.Element(DavNamespace + "prop");
        if (successfulProperty is null)
        {
            return FileProviderResult<ParsedPropertyEntry>.Success(new ParsedPropertyEntry(null));
        }

        var path = PathFromHref(requestUri, href);
        if (!path.IsSuccess)
        {
            return FileProviderResult<ParsedPropertyEntry>.Failure(path.Error!);
        }

        var etag = ParseEtag(successfulProperty.Element(DavNamespace + "getetag")?.Value);
        if (!etag.IsSuccess)
        {
            return FileProviderResult<ParsedPropertyEntry>.Failure(etag.Error!);
        }

        var isDirectory = successfulProperty
            .Element(DavNamespace + "resourcetype")
            ?.Element(DavNamespace + "collection") is not null;
        long? size = null;
        if (!isDirectory)
        {
            var contentLength = successfulProperty.Element(DavNamespace + "getcontentlength")?.Value;
            if (!long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize)
                || parsedSize < 0)
            {
                return Failure<ParsedPropertyEntry>(
                    FileProviderErrorCode.IoFailure,
                    "A WebDAV file returned an invalid content length.");
            }

            size = parsedSize;
        }

        var lastModifiedText = successfulProperty.Element(DavNamespace + "getlastmodified")?.Value;
        DateTimeOffset? lastModified = DateTimeOffset.TryParse(
            lastModifiedText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsedDate)
                ? parsedDate
                : null;
        var location = new FileLocation(ProfileId, Authority, path.Value!, etag.Value);
        return FileProviderResult<ParsedPropertyEntry>.Success(new ParsedPropertyEntry(new FileEntry(
            location,
            isDirectory ? FileEntryKind.Directory : FileEntryKind.File,
            size,
            lastModified,
            etag.Value,
            path.Value!.Name is { } name && name.Value.StartsWith('.'))));
    }

    private FileProviderResult<FilePath> PathFromHref(Uri requestUri, string href)
    {
        if (!Uri.TryCreate(requestUri, href, out var resourceUri)
            || !SameOrigin(_options.BaseUri, resourceUri))
        {
            return Failure<FilePath>(
                FileProviderErrorCode.InvalidLocation,
                "A WebDAV response href escaped the configured origin.");
        }

        var basePath = _options.BaseUri.AbsolutePath;
        var resourcePath = resourceUri.AbsolutePath;
        if (!resourcePath.StartsWith(basePath, StringComparison.Ordinal))
        {
            return Failure<FilePath>(
                FileProviderErrorCode.OutsideRoot,
                "A WebDAV response href escaped the configured base path.");
        }

        var relative = resourcePath[basePath.Length..].TrimEnd('/');
        if (relative.Length == 0)
        {
            return FileProviderResult<FilePath>.Success(FilePath.Root);
        }

        var segments = new List<FilePathSegment>();
        foreach (var encoded in relative.Split('/'))
        {
            try
            {
                segments.Add(new FilePathSegment(Uri.UnescapeDataString(encoded)));
            }
            catch (ArgumentException)
            {
                return Failure<FilePath>(
                    FileProviderErrorCode.InvalidName,
                    "A WebDAV href contains a name that cannot be represented safely.");
            }
        }

        return FileProviderResult<FilePath>.Success(FilePath.FromSegments(segments));
    }

    private static bool SameOrigin(Uri left, Uri right) => string.Equals(left.Scheme, right.Scheme
, StringComparison.Ordinal) && string.Equals(left.IdnHost, right.IdnHost
, StringComparison.Ordinal) && left.Port == right.Port;

    private static bool IsSuccessfulPropertyStatus(XElement propertyStatus)
    {
        var status = propertyStatus.Element(DavNamespace + "status")?.Value;
        return status?.Contains(" 200 ", StringComparison.Ordinal) == true;
    }

    private static FilePath EntryPath(FileEntry entry) =>
        ((FileLocationAddress.Hierarchical)entry.Location.Address).Path;

    private sealed record ParsedPropertyEntry(FileEntry? Entry);

    private static async ValueTask<FileProviderResult<byte[]>> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumPropertyResponseBytes)
        {
            return Failure<byte[]>(
                FileProviderErrorCode.LimitExceeded,
                "The WebDAV property response exceeds the configured bound.");
        }

        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new MemoryStream();
        var copied = await RemoteFileProviderUtilities.CopyAtMostAsync(
            source,
            destination,
            MaximumPropertyResponseBytes + 1,
            bufferSize: 64 * 1024,
            FileTransferStage.Reading,
            progress: null,
            totalBytes: null,
            cancellationToken).ConfigureAwait(false);
        if (copied > MaximumPropertyResponseBytes)
        {
            return Failure<byte[]>(
                FileProviderErrorCode.LimitExceeded,
                "The WebDAV property response exceeds the configured bound.");
        }

        return FileProviderResult<byte[]>.Success(destination.ToArray());
    }
}
