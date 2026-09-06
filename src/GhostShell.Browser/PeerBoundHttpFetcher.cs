using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using GhostShell.Application;

namespace GhostShell.Browser;

/// <summary>
/// Fetches bounded textual resources while connecting TLS to the exact public
/// address set admitted by the destination policy.
/// </summary>
internal sealed class PeerBoundHttpFetcher
{
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan FetchDeadline = TimeSpan.FromSeconds(20);
    private readonly Func<string, CancellationToken, ValueTask<IPAddress[]>>
        _resolveHost;
    private readonly Func<IPAddress, int, CancellationToken, ValueTask<Stream>>
        _connect;

    internal PeerBoundHttpFetcher(IWorkspaceNetworkConnector connector)
        : this(
            new WorkspaceRoutedDnsResolver(connector).ResolveAsync,
            (address, port, cancellationToken) => connector.ConnectTcpAsync(
                address.ToString(),
                port,
                cancellationToken))
    {
        ArgumentNullException.ThrowIfNull(connector);
    }

    internal PeerBoundHttpFetcher(
        Func<string, CancellationToken, ValueTask<IPAddress[]>> resolveHost,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect)
    {
        _resolveHost = resolveHost ?? throw new ArgumentNullException(nameof(resolveHost));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
    }

    public async ValueTask<AgentWebToolExecutionResult> FetchAsync(
        AgentHttpFetchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(FetchDeadline);
        try
        {
            using var handler = CreateHandler();
            using var client = new HttpClient(handler, disposeHandler: false);
            var current = request.Address;
            for (var redirects = 0; ; redirects++)
            {
                using var message = CreateRequest(current, request.Method);
                using var response = await client.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token)
                    .ConfigureAwait(false);
                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= MaximumRedirects)
                    {
                        return Failed(AgentWebToolErrorCode.RedirectLimit);
                    }

                    if (!TryResolveRedirect(current, response.Headers.Location, out current))
                    {
                        return Failed(AgentWebToolErrorCode.InvalidUrl);
                    }

                    continue;
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (request.Method is AgentHttpFetchMethod.Head)
                {
                    return Succeeded(new AgentHttpFetchResult(
                        current.AbsoluteUri,
                        (int)response.StatusCode,
                        mediaType,
                        string.Empty));
                }

                if (!IsSupportedMediaType(mediaType))
                {
                    return Failed(AgentWebToolErrorCode.UnsupportedContentType);
                }

                var content = await ReadContentAsync(response.Content, deadline.Token)
                    .ConfigureAwait(false);
                return Succeeded(new AgentHttpFetchResult(
                    current.AbsoluteUri,
                    (int)response.StatusCode,
                    mediaType,
                    content));
            }
        }
        catch (Exception exception) when (Contains<DestinationDeniedException>(exception))
        {
            return Failed(AgentWebToolErrorCode.DestinationDenied);
        }
        catch (Exception exception) when (Contains<DnsFailureException>(exception))
        {
            return Failed(AgentWebToolErrorCode.DnsFailed);
        }
        catch (BodyTooLargeException)
        {
            return Failed(AgentWebToolErrorCode.BodyTooLarge);
        }
        catch (OperationCanceledException)
        {
            return Failed(
                cancellationToken.IsCancellationRequested
                    ? AgentWebToolErrorCode.Cancelled
                    : AgentWebToolErrorCode.TimedOut);
        }
        catch (Exception exception) when (exception is
            HttpRequestException
            or IOException
            or SocketException
            or DecoderFallbackException)
        {
            return Failed(AgentWebToolErrorCode.Unavailable);
        }
    }

    private SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip
            | DecompressionMethods.Deflate
            | DecompressionMethods.Brotli,
        ConnectCallback = ConnectAsync,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxConnectionsPerServer = 2,
        MaxResponseHeadersLength = 16,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        UseCookies = false,
        UseProxy = false,
    };

    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host.TrimEnd('.');
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _resolveHost(host, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                ArgumentException
                or IOException
                or SocketException)
            {
                throw new DnsFailureException(exception);
            }
        }

        if (addresses.Length == 0)
        {
            throw new DnsFailureException();
        }

        if (addresses.Any(address => !BrowserDestinationPolicy.IsPublicAddress(address)))
        {
            throw new DestinationDeniedException();
        }

        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            try
            {
                return await _connect(address, context.DnsEndPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastFailure = exception;
            }
        }

        throw new HttpRequestException("No approved destination address accepted the connection.", lastFailure);
    }

    private static HttpRequestMessage CreateRequest(
        Uri address,
        AgentHttpFetchMethod method)
    {
        var message = new HttpRequestMessage(
            method is AgentHttpFetchMethod.Head ? HttpMethod.Head : HttpMethod.Get,
            address);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.9));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/*", 0.8));
        message.Headers.UserAgent.ParseAdd("GhostSHELL-Agent-Web/1.0");
        return message;
    }

    private static bool TryResolveRedirect(
        Uri current,
        Uri? location,
        out Uri next)
    {
        next = null!;
        if (location is null
            || !Uri.TryCreate(current, location, out var resolved)
            || !(resolved.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || resolved.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(resolved.UserInfo))
        {
            return false;
        }

        next = new UriBuilder(resolved) { Fragment = string.Empty }.Uri;
        return true;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved
        or HttpStatusCode.Redirect
        or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static bool IsSupportedMediaType(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > AgentHttpFetchResult.MaximumContentBytes)
        {
            throw new BodyTooLargeException();
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[8 * 1_024];
        using var body = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (body.Length + read > AgentHttpFetchResult.MaximumContentBytes)
            {
                throw new BodyTooLargeException();
            }

            body.Write(buffer, 0, read);
        }

        var encoding = ResolveEncoding(content.Headers.ContentType?.CharSet);
        var decoded = encoding.GetString(
            body.GetBuffer(),
            0,
            checked((int)body.Length));
        if (Encoding.UTF8.GetByteCount(decoded) > AgentHttpFetchResult.MaximumContentBytes)
        {
            throw new BodyTooLargeException();
        }

        return decoded;
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return new UTF8Encoding(false, true);
        }

        var normalized = charset.Trim().Trim('"');
        if (normalized.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            return new UTF8Encoding(false, true);
        }

        if (normalized.Equals("us-ascii", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.ASCII;
        }

        if (normalized.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.Latin1;
        }

        throw new HttpRequestException("The response character set is unsupported.");
    }

    private static AgentWebToolExecutionResult Failed(AgentWebToolErrorCode code) =>
        new AgentWebToolExecutionResult.Failed(code);

    private static AgentWebToolExecutionResult Succeeded(AgentWebToolResult result) =>
        new AgentWebToolExecutionResult.Succeeded(result);

    private static bool Contains<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class BodyTooLargeException : Exception;

    private sealed class DestinationDeniedException : Exception;

    private sealed class DnsFailureException : Exception
    {
        public DnsFailureException()
        {
        }

        public DnsFailureException(Exception innerException)
            : base("DNS resolution failed.", innerException)
        {
        }
    }
}
