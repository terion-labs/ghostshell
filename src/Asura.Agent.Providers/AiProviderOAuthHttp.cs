using System.Net;
using System.Net.Http.Headers;
using Asura.Application;

namespace Asura.Agent.Providers;

internal sealed class AiProviderOAuthHttp : IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient _client;
    private bool _disposed;

    public AiProviderOAuthHttp(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? CreateHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public async ValueTask<byte[]> PostFormAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateEndpoint(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Asura/0.1");
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AiProviderOAuthHttpResponse> PostFormResponseAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateEndpoint(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Asura/0.1");
        return await SendResponseAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]> PostJsonAsync(
        Uri endpoint,
        byte[] body,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateEndpoint(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Asura/0.1");
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AiProviderOAuthHttpResponse> PostJsonResponseAsync(
        Uri endpoint,
        byte[] body,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateEndpoint(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Asura/0.1");
        return await SendResponseAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]> GetGitHubCopilotTokenAsync(
        Uri endpoint,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateEndpoint(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        try
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            GitHubCopilotHttpHeaders.Apply(request.Headers);

            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException exception)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError,
                innerException: exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }

    private async ValueTask<byte[]> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await SendResponseAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 200 and <= 299)
        {
            return response.Body;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.AuthenticationFailed),
            HttpStatusCode.Forbidden => AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.AccessDenied),
            HttpStatusCode.NotFound => AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ModelUnavailable),
            HttpStatusCode.TooManyRequests => AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.RateLimited),
            >= HttpStatusCode.InternalServerError => AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProviderUnavailable),
            _ => AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError),
        };
    }

    private async ValueTask<AiProviderOAuthHttpResponse> SendResponseAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProviderUnavailable,
                innerException: exception);
        }

        using (response)
        {
            if (response.Content.Headers.ContentLength is { } length
                && length > MaximumResponseBytes)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var limited = new LimitedReadStream(source, MaximumResponseBytes);
            using var output = new BoundedMemoryStream(MaximumResponseBytes);
            await limited.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return new AiProviderOAuthHttpResponse(response.StatusCode, output.ToArray());
        }
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps
, StringComparison.Ordinal) || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        ActivityHeadersPropagator = null,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.Brotli
            | DecompressionMethods.Deflate
            | DecompressionMethods.GZip,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        Credentials = null,
        MaxConnectionsPerServer = 2,
        MaxResponseHeadersLength = 32,
        PreAuthenticate = false,
        Proxy = null,
        UseCookies = false,
        UseProxy = false,
    };
}

internal sealed record AiProviderOAuthHttpResponse(
    HttpStatusCode StatusCode,
    byte[] Body);
