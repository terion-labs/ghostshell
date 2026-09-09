using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Asura.Files.Tests;

internal sealed class FakeWebDavHandler : HttpMessageHandler
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly Dictionary<string, Resource> _resources = new(StringComparer.Ordinal);
    private long _revision;

    public FakeWebDavHandler()
    {
        _resources["/root"] = NewResource(isDirectory: true, []);
    }

    public int CopyRequests { get; private set; }

    public int GetRequests { get; private set; }

    public bool IgnoreRangeRequests { get; set; }

    public bool ReturnMismatchedContentRange { get; set; }

    public string? LastDeleteAbsolutePath { get; private set; }

    public bool Contains(string path) => _resources.ContainsKey(Normalize(path));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Normalize(request.RequestUri!.AbsolutePath);
        var response = request.Method.Method switch
        {
            "PROPFIND" => PropFind(path, request),
            "GET" => Get(path, request),
            "PUT" => await PutAsync(path, request, cancellationToken),
            "MKCOL" => MkCol(path),
            "COPY" => CopyMove(path, request, move: false),
            "MOVE" => CopyMove(path, request, move: true),
            "DELETE" => Delete(path, request),
            _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
        };
        response.RequestMessage = request;
        return response;
    }

    private HttpResponseMessage PropFind(string path, HttpRequestMessage request)
    {
        if (!_resources.TryGetValue(path, out var target))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var depth = request.Headers.TryGetValues("Depth", out var values)
            ? values.Single()
            : "infinity";
        var selected = new List<KeyValuePair<string, Resource>>
        {
            new(path, target),
        };
        if (string.Equals(depth, "1", StringComparison.Ordinal))
        {
            selected.AddRange(_resources
                .Where(pair => !string.Equals(pair.Key, path, StringComparison.Ordinal) && string.Equals(Parent(pair.Key), path, StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal));
        }

        var multistatus = new XElement(
            Dav + "multistatus",
            selected.Select(pair => PropertyResponse(pair.Key, pair.Value)));
        var content = new StringContent(
            new XDocument(new XDeclaration("1.0", "utf-8", null), multistatus).ToString(),
            Encoding.UTF8,
            "application/xml");
        return new HttpResponseMessage((HttpStatusCode)207) { Content = content };
    }

    private HttpResponseMessage Get(string path, HttpRequestMessage request)
    {
        GetRequests++;
        if (!_resources.TryGetValue(path, out var resource))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (resource.IsDirectory)
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        var condition = CheckHttpCondition(resource, request);
        if (condition is not null)
        {
            return condition;
        }

        if (IgnoreRangeRequests)
        {
            var full = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(resource.Content),
            };
            full.Headers.ETag = EntityTagHeaderValue.Parse(resource.ETag);
            return full;
        }

        var start = request.Headers.Range?.Ranges.Single().From ?? 0;
        var end = request.Headers.Range?.Ranges.Single().To ?? (resource.Content.LongLength - 1);
        if (start > resource.Content.LongLength || end < start)
        {
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        }

        var count = checked((int)Math.Min(end - start + 1, resource.Content.LongLength - start));
        var bytes = resource.Content.AsSpan(checked((int)start), count).ToArray();
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Headers.ETag = EntityTagHeaderValue.Parse(resource.ETag);
        response.Content.Headers.ContentRange = ReturnMismatchedContentRange
            ? new ContentRangeHeaderValue(0, count - 1, resource.Content.LongLength)
            : new ContentRangeHeaderValue(
                start,
                start + count - 1,
                resource.Content.LongLength);
        return response;
    }

    private async Task<HttpResponseMessage> PutAsync(
        string path,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _resources.TryGetValue(path, out var existing);
        var condition = CheckHttpCondition(existing, request);
        if (condition is not null)
        {
            return condition;
        }

        if (!_resources.TryGetValue(Parent(path), out var parent) || !parent.IsDirectory)
        {
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        }

        var content = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        var resource = NewResource(isDirectory: false, content);
        _resources[path] = resource;
        TouchParent(path);
        var response = new HttpResponseMessage(
            existing is null ? HttpStatusCode.Created : HttpStatusCode.NoContent);
        response.Headers.ETag = EntityTagHeaderValue.Parse(resource.ETag);
        return response;
    }

    private HttpResponseMessage MkCol(string path)
    {
        if (_resources.ContainsKey(path))
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        if (!_resources.TryGetValue(Parent(path), out var parent) || !parent.IsDirectory)
        {
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        }

        _resources[path] = NewResource(isDirectory: true, []);
        TouchParent(path);
        return new HttpResponseMessage(HttpStatusCode.Created);
    }

    private HttpResponseMessage CopyMove(
        string sourcePath,
        HttpRequestMessage request,
        bool move)
    {
        if (!_resources.TryGetValue(sourcePath, out var source))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (!move)
        {
            CopyRequests++;
        }

        var destinationHeader = request.Headers.GetValues("Destination").Single();
        var destinationPath = Normalize(new Uri(destinationHeader).AbsolutePath);
        _resources.TryGetValue(destinationPath, out var existing);
        var overwrite = string.Equals(request.Headers.GetValues("Overwrite").Single(), "T", StringComparison.Ordinal);
        if (!overwrite && existing is not null)
        {
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        }

        var ifHeader = request.Headers.GetValues("If").Single();
        if (!ifHeader.Contains($"[{source.ETag}]", StringComparison.Ordinal)
            || existing is not null && !ifHeader.Contains($"[{existing.ETag}]", StringComparison.Ordinal)
                && ifHeader.Contains($"<{new Uri(destinationHeader).AbsoluteUri}>", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        }

        if (!_resources.TryGetValue(Parent(destinationPath), out var parent) || !parent.IsDirectory)
        {
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        }

        _resources[destinationPath] = NewResource(source.IsDirectory, [.. source.Content]);
        if (move)
        {
            _resources.Remove(sourcePath);
            RemoveDescendants(sourcePath);
            TouchParent(sourcePath);
        }

        TouchParent(destinationPath);
        return new HttpResponseMessage(
            existing is null ? HttpStatusCode.Created : HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Delete(string path, HttpRequestMessage request)
    {
        LastDeleteAbsolutePath = request.RequestUri!.AbsolutePath;
        if (!_resources.TryGetValue(path, out var resource))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var condition = CheckHttpCondition(resource, request);
        if (condition is not null)
        {
            return condition;
        }

        _resources.Remove(path);
        RemoveDescendants(path);
        TouchParent(path);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private static HttpResponseMessage? CheckHttpCondition(
        Resource? resource,
        HttpRequestMessage request)
    {
        var ifNoneMatch = Header(request, "If-None-Match");
        if (string.Equals(ifNoneMatch, "*", StringComparison.Ordinal) && resource is not null)
        {
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        }

        var ifMatch = Header(request, "If-Match");
        if (string.Equals(ifMatch, "*", StringComparison.Ordinal) && resource is null)
        {
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        }

        if (ifMatch is not null and not "*" && !string.Equals(resource?.ETag, ifMatch, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        }

        return null;
    }

    private XElement PropertyResponse(string path, Resource resource)
    {
        var href = $"https://dav.test{EscapePath(path)}{(resource.IsDirectory ? "/" : string.Empty)}";
        return new XElement(
            Dav + "response",
            new XElement(Dav + "href", href),
            new XElement(
                Dav + "propstat",
                new XElement(
                    Dav + "prop",
                    new XElement(
                        Dav + "resourcetype",
                        resource.IsDirectory ? new XElement(Dav + "collection") : null),
                    new XElement(Dav + "getcontentlength", resource.Content.LongLength),
                    new XElement(Dav + "getlastmodified", resource.LastModifiedAt.ToString("R")),
                    new XElement(Dav + "getetag", resource.ETag)),
                new XElement(Dav + "status", "HTTP/1.1 200 OK")));
    }

    private Resource NewResource(bool isDirectory, byte[] content)
    {
        var revision = Interlocked.Increment(ref _revision);
        return new Resource(
            isDirectory,
            content,
            $"\"dav-{revision}\"",
            DateTimeOffset.UnixEpoch.AddTicks(revision));
    }

    private void TouchParent(string path)
    {
        var parentPath = Parent(path);
        if (_resources.TryGetValue(parentPath, out var parent))
        {
            _resources[parentPath] = NewResource(isDirectory: true, parent.Content);
        }
    }

    private void RemoveDescendants(string path)
    {
        foreach (var child in _resources.Keys
                     .Where(key => key.StartsWith($"{path}/", StringComparison.Ordinal))
                     .ToArray())
        {
            _resources.Remove(child);
        }
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static string Normalize(string path)
    {
        var unescaped = Uri.UnescapeDataString(path).TrimEnd('/');
        return unescaped.Length == 0 ? "/" : unescaped;
    }

    private static string Parent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private sealed record Resource(
        bool IsDirectory,
        byte[] Content,
        string ETag,
        DateTimeOffset LastModifiedAt);
}
