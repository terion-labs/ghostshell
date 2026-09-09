using System.Net;
using System.Net.Http.Headers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Asura.Mcp;

/// <summary>
/// Streamable HTTP boundary for the official MCP client. It pins the endpoint
/// origin, refuses redirects, injects only explicitly resolved vault-backed
/// headers, and caps every HTTP response body before the SDK parses it.
/// </summary>
internal sealed class BoundedStreamableHttpClientTransport :
    IMcpClientTransportBoundary
{
    private const int MaximumSessionIdLength = 1024;

    private readonly HttpClient _httpClient;
    private readonly HttpClientTransport _transport;
    private int _disposed;

    public BoundedStreamableHttpClientTransport(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        McpSessionOptions options,
        HttpMessageHandler? innerHandler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var handler = new McpHttpBoundaryHandler(
            endpoint,
            headers,
            options.MaxMessageBytes,
            innerHandler ?? CreatePrimaryHandler(options));
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = "Asura MCP Streamable HTTP",
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = options.InitializationTimeout,
                MaxReconnectionAttempts = 2,
            },
            _httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
    }

    public string Name => "Asura MCP Streamable HTTP";

    public bool CleanupUncertain => false;

    public McpStderrDiagnostics Diagnostics => new(0, 0, false, false);

    public Task<ITransport> ConnectAsync(
        CancellationToken cancellationToken = default) =>
        _transport.ConnectAsync(cancellationToken);

    public void ResetIncomingMessageBudget()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        // HTTP response bodies have a stricter aggregate byte bound. The SDK
        // owns SSE event parsing inside that envelope.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _httpClient.Dispose();
        }
    }

    private static HttpMessageHandler CreatePrimaryHandler(
        McpSessionOptions options) =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.InitializationTimeout,
            MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            UseProxy = false,
        };

    private sealed class McpHttpBoundaryHandler : DelegatingHandler
    {
        private readonly Uri _endpoint;
        private readonly Dictionary<string, string> _headers;
        private readonly int _maximumResponseBytes;

        public McpHttpBoundaryHandler(
            Uri endpoint,
            IReadOnlyDictionary<string, string> headers,
            int maximumResponseBytes,
            HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
            _endpoint = endpoint;
            _headers = new Dictionary<string, string>(
                headers,
                StringComparer.OrdinalIgnoreCase);
            _maximumResponseBytes = maximumResponseBytes;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } requestUri
                || !HasSameOrigin(_endpoint, requestUri))
            {
                throw new McpTransportFailureException(
                    McpErrorCode.TransportFailed,
                    "The MCP HTTP transport refused a cross-origin request.");
            }

            foreach (var header in _headers)
            {
                if (request.Headers.Contains(header.Key)
                    || !request.Headers.TryAddWithoutValidation(
                        header.Key,
                        header.Value))
                {
                    throw new McpTransportFailureException(
                        McpErrorCode.TransportFailed,
                        "The MCP HTTP transport could not apply a configured secret header.");
                }
            }

            var response = await base.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                response.Dispose();
                throw new McpTransportFailureException(
                    McpErrorCode.TransportFailed,
                    "The MCP HTTP transport refused a redirect.");
            }

            if (!HasValidSessionId(response.Headers))
            {
                response.Dispose();
                throw new McpTransportFailureException(
                    McpErrorCode.InvalidMessage,
                    "The MCP server returned an invalid session identifier.");
            }

            if (response.Content is { } content)
            {
                if (content.Headers.ContentLength is { } contentLength
                    && contentLength > _maximumResponseBytes)
                {
                    response.Dispose();
                    throw new McpTransportFailureException(
                        McpErrorCode.MessageTooLarge,
                        "An MCP HTTP response exceeded the configured byte limit.");
                }

                response.Content = new BoundedHttpContent(
                    content,
                    _maximumResponseBytes);
            }

            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _headers.Clear();
            }

            base.Dispose(disposing);
        }

        private static bool HasSameOrigin(Uri expected, Uri candidate) =>
            string.Equals(
                expected.Scheme,
                candidate.Scheme,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                expected.IdnHost,
                candidate.IdnHost,
                StringComparison.OrdinalIgnoreCase)
            && EffectivePort(expected) == EffectivePort(candidate);

        private static int EffectivePort(Uri uri) =>
            uri.IsDefaultPort
                ? string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ? 443 : 80
                : uri.Port;

        private static bool IsRedirect(HttpStatusCode statusCode) =>
            (int)statusCode is >= 300 and <= 399;

        private static bool HasValidSessionId(HttpResponseHeaders headers)
        {
            if (!headers.TryGetValues("MCP-Session-Id", out var values))
            {
                return true;
            }

            var sessionIds = values.Take(2).ToArray();
            return sessionIds.Length == 1
                && sessionIds[0].Length is > 0 and <= MaximumSessionIdLength
                && !sessionIds[0].Any(char.IsControl);
        }
    }
}
