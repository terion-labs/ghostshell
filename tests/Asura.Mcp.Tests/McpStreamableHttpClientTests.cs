using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Asura.Mcp.Tests;

public sealed class McpStreamableHttpClientTests
{
    private static readonly Uri Endpoint =
        new("https://mcp.example.test/rpc");

    [Fact]
    public async Task ConnectListAndCall_UsesPinnedStreamableHttpSession()
    {
        var handler = new FakeMcpHttpHandler(callResponseUsesSse: true);
        var connected = await McpStreamableHttpClient.ConnectAsync(
            Endpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer vault-backed-token",
            },
            new McpClientInfo("asura-tests", "1.0.0"),
            handler: handler);

        Assert.True(connected.IsSuccess, connected.Error?.Message);
        await using var client = connected.Value!;
        var tools = await client.ListToolsAsync();
        Assert.True(tools.IsSuccess, tools.Error?.Message);
        Assert.Equal(["first", "second"], tools.Value!.Select(tool => tool.Name), StringComparer.Ordinal);

        using var arguments = JsonDocument.Parse("""{"value":"hello"}""");
        var called = await client.CallToolAsync(
            "first",
            arguments.RootElement);

        Assert.True(called.IsSuccess, called.Error?.Message);
        Assert.Equal(
            "ok",
            called.Value!.StructuredContent!.Value
                .GetProperty("status")
                .GetString());
        var posts = handler.Requests
            .Where(request => request.Method == HttpMethod.Post)
            .ToArray();
        Assert.True(posts.Length >= 5);
        Assert.All(posts, request =>
        {
            Assert.Equal(Endpoint, request.Uri);
            Assert.Contains("application/json", request.Accept, StringComparer.Ordinal);
            Assert.Contains("text/event-stream", request.Accept, StringComparer.Ordinal);
            Assert.Equal(
                "Bearer vault-backed-token",
                request.Authorization);
        });
        Assert.All(
            posts.Where(request => !string.Equals(request.MethodName, "initialize", StringComparison.Ordinal)),
            request =>
            {
                Assert.Equal("session-1", request.SessionId);
                Assert.Equal(McpProtocol.Version, request.ProtocolVersion);
            });
    }

    [Fact]
    public async Task Redirect_IsRejectedWithoutFollowingLocation()
    {
        var handler = new RedirectHandler();

        var connected = await McpStreamableHttpClient.ConnectAsync(
            Endpoint,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new McpClientInfo("asura-tests", "1.0.0"),
            handler: handler);

        Assert.False(connected.IsSuccess);
        Assert.Equal(McpErrorCode.TransportFailed, connected.Error!.Code);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task OversizedResponse_IsRejectedBeforeProtocolParsing()
    {
        var handler = new OversizedHandler();
        var options = new McpSessionOptions
        {
            MaxMessageBytes = 1024,
            MaxToolSchemaBytes = 512,
            MaxToolArgumentsBytes = 512,
            MaxToolResultBytes = 512,
        };

        var connected = await McpStreamableHttpClient.ConnectAsync(
            Endpoint,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new McpClientInfo("asura-tests", "1.0.0"),
            options,
            handler);

        Assert.False(connected.IsSuccess);
        Assert.Equal(McpErrorCode.MessageTooLarge, connected.Error!.Code);
    }

    [Fact]
    public async Task StalledChunkedJsonResponse_HonorsCancellation()
    {
        var stream = new StallingJsonStream();
        var handler = new StallingJsonHandler(stream);
        using var cancellation = new CancellationTokenSource();
        var connecting = McpStreamableHttpClient.ConnectAsync(
            Endpoint,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new McpClientInfo("asura-tests", "1.0.0"),
            handler: handler,
            cancellationToken: cancellation.Token);

        await stream.ReadStalled.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var connected = await connecting.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(connected.IsSuccess);
        Assert.Equal(McpErrorCode.Cancelled, connected.Error!.Code);
    }

    internal sealed class FakeMcpHttpHandler(bool callResponseUsesSse)
        : HttpMessageHandler
    {
        private int _nextCursorSeen;

        public ConcurrentQueue<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            string? methodName = null;
            JsonElement? id = null;
            JsonDocument? document = null;
            if (!string.IsNullOrEmpty(body))
            {
                document = JsonDocument.Parse(body);
                var root = document.RootElement;
                methodName = root.TryGetProperty("method", out var method)
                    ? method.GetString()
                    : null;
                id = root.TryGetProperty("id", out var requestId)
                    ? requestId.Clone()
                    : null;
            }

            using (document)
            {
                Requests.Enqueue(new RequestSnapshot(
                    request.Method,
                    request.RequestUri!,
                    methodName,
                    Header(request.Headers, "MCP-Session-Id"),
                    Header(request.Headers, "MCP-Protocol-Version"),
                    [.. request.Headers.Accept.Select(value => value.MediaType!)],
                    Header(request.Headers, "Authorization")));

                return (request.Method.Method, methodName) switch
                {
                    ("POST", "initialize") => JsonResponse(
                        Result(id, """
                            {"protocolVersion":"2025-11-25","capabilities":{"tools":{"listChanged":false}},"serverInfo":{"name":"remote-test","version":"1.0.0"}}
                            """),
                        sessionId: "session-1"),
                    ("POST", "notifications/initialized") =>
                        new HttpResponseMessage(HttpStatusCode.Accepted),
                    ("POST", "tools/list") => ToolsPage(id),
                    ("POST", "tools/call") when callResponseUsesSse =>
                        SseResponse(Result(id, """
                            {"content":[{"type":"text","text":"done"}],"structuredContent":{"status":"ok"},"isError":false}
                            """)),
                    ("POST", "tools/call") => JsonResponse(Result(id, """
                        {"content":[{"type":"text","text":"done"}],"structuredContent":{"status":"ok"},"isError":false}
                        """)),
                    ("DELETE", _) =>
                        new HttpResponseMessage(HttpStatusCode.NoContent),
                    _ => new HttpResponseMessage(
                        HttpStatusCode.MethodNotAllowed),
                };
            }
        }

        private HttpResponseMessage ToolsPage(JsonElement? id)
        {
            var firstPage = Interlocked.Exchange(
                ref _nextCursorSeen,
                1) == 0;
            return JsonResponse(Result(
                id,
                firstPage
                    ? """
                      {"tools":[{"name":"first","description":"First tool","inputSchema":{"type":"object"}}],"nextCursor":"next"}
                      """
                    : """
                      {"tools":[{"name":"second","description":"Second tool","inputSchema":{"type":"object"}}]}
                      """));
        }

        private static string Result(JsonElement? id, string result) =>
            $$"""{"jsonrpc":"2.0","id":{{id?.GetRawText()}},"result":{{result}}}""";

        private static HttpResponseMessage JsonResponse(
            string json,
            string? sessionId = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"),
            };
            if (sessionId is not null)
            {
                response.Headers.TryAddWithoutValidation(
                    "MCP-Session-Id",
                    sessionId);
            }

            return response;
        }

        private static HttpResponseMessage SseResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"event: message\ndata: {json}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };

        private static string? Header(
            HttpRequestHeaders headers,
            string name) =>
            headers.TryGetValues(name, out var values)
                ? values.Single()
                : null;
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(
                HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri(
                "https://attacker.example.test/mcp");
            return Task.FromResult(response);
        }
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[1025]),
            });
    }

    private sealed class StallingJsonHandler(StallingJsonStream stream)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }

    private sealed class StallingJsonStream : Stream
    {
        private static readonly byte[] Prefix = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"result":""");

        private readonly TaskCompletionSource _readStalled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public Task ReadStalled => _readStalled.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < Prefix.Length)
            {
                var length = Math.Min(
                    Prefix.Length - _position,
                    buffer.Length);
                Prefix.AsSpan(_position, length).CopyTo(buffer.Span);
                _position += length;
                return length;
            }

            _readStalled.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(
                buffer.AsMemory(offset, count),
                cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    public sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        string? MethodName,
        string? SessionId,
        string? ProtocolVersion,
        IReadOnlyList<string> Accept,
        string? Authorization);
}
