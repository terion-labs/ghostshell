using System.Net;
using System.Text.Json.Serialization;

namespace Asura.ConnectionBackend;

internal sealed record WorkspaceHttpHeader(string Name, string[] Values, bool Content = false);
internal sealed record WorkspaceHttpRequest(string Method, string Address, WorkspaceHttpHeader[] Headers, bool HasContent);
internal sealed record WorkspaceHttpResponse(int Status, WorkspaceHttpHeader[] Headers);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WorkspaceHttpRequest))]
[JsonSerializable(typeof(WorkspaceHttpResponse))]
internal sealed partial class WorkspaceHttpJsonContext : JsonSerializerContext;

/// <summary>One HTTP exchange per private backend process, without cookies, redirects, ambient credentials or proxies.</summary>
internal static class WorkspaceHttpProtocol
{
    internal const int ChunkBytes = 64 * 1024;
    internal const int RequestBytes = 32 * 1024 * 1024;
    internal const int ResponseBytes = 128 * 1024 * 1024;

    internal static void Validate(WorkspaceHttpHeader[] headers)
    {
        if (headers is null || headers.Length > 128) { throw new InvalidDataException("Invalid HTTP headers."); }
        var length = 0;
        foreach (var header in headers)
        {
            if (header is null || string.IsNullOrWhiteSpace(header.Name) || header.Name.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
                || header.Values is not { Length: > 0 and <= 32 }) { throw new InvalidDataException("Invalid HTTP header."); }
            length = checked(length + header.Name.Length);
            foreach (var value in header.Values)
            {
                if (value is null || value.IndexOfAny(['\r', '\n', '\0']) >= 0) { throw new InvalidDataException("Invalid HTTP header value."); }
                length = checked(length + value.Length);
            }
        }
        if (length > ChunkBytes) { throw new InvalidDataException("HTTP headers exceed their limit."); }
    }

    internal static async Task WriteBodyAsync(Stream source, Stream output, int limit, CancellationToken token)
    {
        var buffer = new byte[ChunkBytes];
        long count = 0;
        while (true)
        {
            var length = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (length == 0) { break; }
            count += length;
            if (count > limit) { throw new InvalidDataException("The backend HTTP body exceeds its limit."); }
            await DatabaseOperationProtocol.WriteFrameAsync(output, buffer.AsMemory(0, length), token).ConfigureAwait(false);
        }
        await DatabaseOperationProtocol.WriteFrameAsync(output, ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
    }

    internal static async Task RunChildAsync(Stream input, Stream output, CancellationToken token)
    {
        var request = await BackendJsonFrames.ReadAsync(input, WorkspaceHttpJsonContext.Default.WorkspaceHttpRequest, token).ConfigureAwait(false);
        if (request.Address is null || request.Address.Length > 8192 || !Uri.TryCreate(request.Address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || request.Method is not ("GET" or "POST" or "HEAD" or "PUT" or "PATCH" or "DELETE" or "OPTIONS"))
        {
            throw new InvalidDataException("The backend HTTP request is invalid.");
        }
        Validate(request.Headers);
        using var body = new MemoryStream();
        while (true)
        {
            var chunk = await DatabaseOperationProtocol.ReadFrameAsync(input, ChunkBytes, token).ConfigureAwait(false);
            if (chunk.Length == 0) { break; }
            if (!request.HasContent || body.Length > RequestBytes - chunk.Length) { throw new InvalidDataException("The backend HTTP request body is invalid."); }
            await body.WriteAsync(chunk, token).ConfigureAwait(false);
        }
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = request.HasContent ? new ByteArrayContent(body.GetBuffer(), 0, checked((int)body.Length)) : null,
        };
        foreach (var header in request.Headers)
        {
            System.Net.Http.Headers.HttpHeaders? headers = header.Content ? message.Content?.Headers : message.Headers;
            if (headers is null || !headers.TryAddWithoutValidation(header.Name, header.Values)) { throw new InvalidDataException("Invalid backend HTTP header placement."); }
        }
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            ActivityHeadersPropagator = null,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxResponseHeadersLength = 64,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await DatabaseOperationProtocol.WriteFrameAsync(output, "ready"u8.ToArray(), token).ConfigureAwait(false);
        await DatabaseOperationProtocol.ExpectAsync(input, "execute"u8.ToArray(), token).ConfigureAwait(false);
        // Nothing above dispatch can contact an origin. No retry is performed
        // after this boundary, including a lost response to POST/PUT/DELETE.
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        WorkspaceHttpHeader[] responseHeaders = [.. response.Headers.Select(header => new WorkspaceHttpHeader(header.Key, [.. header.Value])),
            .. response.Content.Headers.Select(header => new WorkspaceHttpHeader(header.Key, [.. header.Value], Content: true))];
        Validate(responseHeaders);
        await BackendJsonFrames.WriteAsync(output, new WorkspaceHttpResponse((int)response.StatusCode, responseHeaders),
            WorkspaceHttpJsonContext.Default.WorkspaceHttpResponse, token).ConfigureAwait(false);
        await using var responseBody = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await WriteBodyAsync(responseBody, output, ResponseBytes, token).ConfigureAwait(false);
    }
}
