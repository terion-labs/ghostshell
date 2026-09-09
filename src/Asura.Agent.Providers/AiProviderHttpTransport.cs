using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Providers;

internal sealed class AiProviderHttpTransport : IDisposable
{
    private const int MaximumCredentialBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly ISecretVault _secretVault;
    private readonly AiProviderOAuthCredentialSource _oauthCredentials;
    private readonly HttpClient _client;
    private static readonly HttpRequestOptionsKey<Uri> ExpectedEndpointKey =
        new("Asura.AiProvider.ExpectedEndpoint");
    private bool _disposed;

    public AiProviderHttpTransport(
        ISecretVault secretVault,
        HttpMessageHandler? handler = null,
        AiProviderOAuthOptions? oauthOptions = null,
        HttpMessageHandler? oauthHandler = null,
        TimeProvider? timeProvider = null)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _oauthCredentials = new AiProviderOAuthCredentialSource(
            secretVault,
            oauthOptions ?? new AiProviderOAuthOptions(),
            oauthHandler,
            timeProvider);
        _client = new HttpClient(handler ?? CreateHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public async ValueTask<HttpRequestMessage> CreateRequestAsync(
        AiProviderProfile profile,
        HttpMethod method,
        string relativePath,
        string acceptMediaType,
        byte[]? body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptMediaType);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var requestEndpoint = profile.Endpoint;
        AiProviderRequestCredential? oauthCredential = null;
        if (profile.Authentication is AiProviderAuthentication.OAuth oauth)
        {
            oauthCredential = await _oauthCredentials.ResolveAsync(
                profile,
                oauth,
                cancellationToken).ConfigureAwait(false);
            requestEndpoint = oauthCredential.Endpoint;
        }
        else if (profile.Authentication is AiProviderAuthentication.AwsCredentialChain)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        var request = new HttpRequestMessage(
            method,
            CreateRequestUri(requestEndpoint, relativePath))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        try
        {
            request.Options.Set(ExpectedEndpointKey, requestEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptMediaType));
            request.Headers.UserAgent.ParseAdd("Asura/0.1");
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = "utf-8",
                };
            }

            var definition = AiProviderCatalog.Get(profile.Identity);
            if (profile.Protocol == AiProviderProtocol.AnthropicMessages)
            {
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }

            if (profile.Identity == AiProviderKind.GitHubCopilot)
            {
                GitHubCopilotHttpHeaders.Apply(request.Headers);
                request.Headers.TryAddWithoutValidation(
                    "Openai-Intent",
                    "conversation-edits");
                if (string.Equals(relativePath, "models", StringComparison.Ordinal))
                {
                    GitHubCopilotHttpHeaders.ApplyModelCatalogVersion(request.Headers);
                }
            }

            if (profile.Authentication is AiProviderAuthentication.ApiKey apiKey)
            {
                var credential = await ResolveCredentialAsync(
                    profile.Id,
                    apiKey.Secret,
                    cancellationToken).ConfigureAwait(false);
                switch (definition.CredentialPlacement)
                {
                    case AiProviderCredentialPlacement.AuthorizationBearer:
                        request.Headers.Authorization = new AuthenticationHeaderValue(
                            "Bearer",
                            credential);
                        break;
                    case AiProviderCredentialPlacement.AnthropicApiKeyHeader:
                        request.Headers.TryAddWithoutValidation("x-api-key", credential);
                        break;
                    case AiProviderCredentialPlacement.GoogleApiKeyHeader:
                        request.Headers.TryAddWithoutValidation("x-goog-api-key", credential);
                        break;
                    default:
                        throw AiProviderClientException.Create(
                            AiProviderRuntimeErrorCode.InvalidConfiguration);
                }
            }
            else if (oauthCredential is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    oauthCredential.AccessToken);
                if (profile.Identity == AiProviderKind.OpenAi)
                {
                    request.Headers.TryAddWithoutValidation("originator", "asura");
                    request.Headers.TryAddWithoutValidation(
                        "OpenAI-Beta",
                        "responses=experimental");
                    if (oauthCredential.AccountId is { } accountId)
                    {
                        request.Headers.TryAddWithoutValidation(
                            "ChatGPT-Account-Id",
                            accountId);
                    }
                }
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    public async ValueTask<HttpResponseMessage> SendAsync(
        AiProviderProfile profile,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Options.TryGetValue(ExpectedEndpointKey, out var expectedEndpoint)
            || request.RequestUri is null
            || !HasSameOrigin(expectedEndpoint, request.RequestUri))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

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

        try
        {
            var responseUri = response.RequestMessage?.RequestUri;
            if (responseUri is null || !HasSameOrigin(expectedEndpoint, responseUri))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError);
            }

            if (response.StatusCode is >= HttpStatusCode.MultipleChoices
                and < HttpStatusCode.BadRequest)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw CreateStatusFailure(response);
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public static void ValidateContent(
        HttpResponseMessage response,
        string expectedMediaType,
        int maximumBytes,
        bool allowMissingMediaType = false)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMediaType);
        if (response.Content.Headers.ContentLength is { } contentLength
            && contentLength > maximumBytes)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ResponseTooLarge);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var matches = allowMissingMediaType && mediaType is null
            || string.Equals(
                mediaType,
                expectedMediaType,
                StringComparison.OrdinalIgnoreCase);
        if (!matches
            && string.Equals(expectedMediaType, "application/json", StringComparison.Ordinal)
            && mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true)
        {
            matches = true;
        }

        if (!matches)
        {
            throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
        }
    }

    public static Uri CreateRequestUri(Uri endpoint, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!Uri.TryCreate(relativePath, UriKind.Relative, out var relative))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        var requestUri = new Uri(endpoint, relative);
        if (!HasSameOrigin(endpoint, requestUri)
            || !requestUri.AbsolutePath.StartsWith(
                endpoint.AbsolutePath,
                StringComparison.Ordinal))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        return requestUri;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _oauthCredentials.Dispose();
        _client.Dispose();
    }

    internal static SocketsHttpHandler CreateHandler(IWebProxy? proxy = null) => new()
    {
        ActivityHeadersPropagator = null,
        AllowAutoRedirect = false,
        AutomaticDecompression =
            DecompressionMethods.Brotli
            | DecompressionMethods.Deflate
            | DecompressionMethods.GZip,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        Credentials = null,
        MaxConnectionsPerServer = 4,
        MaxResponseHeadersLength = 64,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PreAuthenticate = false,
        Proxy = proxy,
        UseCookies = false,
        UseProxy = proxy is not null,
    };

    private async ValueTask<string> ResolveCredentialAsync(
        AiProviderProfileId profileId,
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        var result = await _secretVault.ResolveAsync(
            new ResolveSecretRequest(
                reference,
                new SecretScope(SecretScopeKind.AiProvider, profileId.Value),
                new SecretUsePurpose(
                    SecretUseKind.AiProviderAuthentication,
                    profileId.Value)),
            cancellationToken).ConfigureAwait(false);
        if (result is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            throw MapVaultFailure(failure.Error.Code);
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        if (material.Length > MaximumCredentialBytes)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.CredentialUnavailable);
        }

        var buffer = new byte[material.Length];
        try
        {
            material.CopyTo(buffer);
            string credential;
            try
            {
                credential = StrictUtf8.GetString(buffer);
            }
            catch (DecoderFallbackException exception)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.CredentialUnavailable,
                    innerException: exception);
            }

            if (credential.Length == 0
                || credential.Any(character =>
                    char.IsControl(character) || char.IsWhiteSpace(character)))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.CredentialUnavailable);
            }

            return credential;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static AiProviderClientException MapVaultFailure(SecretVaultErrorCode code) =>
        code is SecretVaultErrorCode.Cancelled or SecretVaultErrorCode.UserCancelled
            ? AiProviderClientException.Create(AiProviderRuntimeErrorCode.Cancelled)
            : AiProviderClientException.Create(AiProviderRuntimeErrorCode.CredentialUnavailable);

    private static AiProviderClientException CreateStatusFailure(
        HttpResponseMessage response)
    {
        var retryAfter = RetryAfter(response.Headers.RetryAfter);
        var errorCode = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => AiProviderRuntimeErrorCode.AuthenticationFailed,
            HttpStatusCode.Forbidden => AiProviderRuntimeErrorCode.AccessDenied,
            HttpStatusCode.PaymentRequired => AiProviderRuntimeErrorCode.QuotaExceeded,
            HttpStatusCode.NotFound => AiProviderRuntimeErrorCode.ModelUnavailable,
            HttpStatusCode.RequestTimeout => AiProviderRuntimeErrorCode.Timeout,
            HttpStatusCode.RequestEntityTooLarge =>
                AiProviderRuntimeErrorCode.ResponseTooLarge,
            HttpStatusCode.TooManyRequests => AiProviderRuntimeErrorCode.RateLimited,
            >= HttpStatusCode.InternalServerError =>
                AiProviderRuntimeErrorCode.ProviderUnavailable,
            _ => AiProviderRuntimeErrorCode.ProtocolError,
        };
        return AiProviderClientException.Create(errorCode, retryAfter);
    }

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        var value = header?.Delta;
        if (value is null && header?.Date is { } date)
        {
            value = date - DateTimeOffset.UtcNow;
        }

        return value is { } retry
            && retry > TimeSpan.Zero
            && retry <= TimeSpan.FromDays(1)
                ? retry
                : null;
    }

    private static bool HasSameOrigin(Uri expected, Uri actual) =>
        string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.IdnHost, actual.IdnHost, StringComparison.OrdinalIgnoreCase)
        && expected.Port == actual.Port;
}
