using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Providers;

/// <summary>
/// Bounded OpenAI and GitHub interactive authentication. Every completed flow
/// writes its refreshable session directly to the OS vault and returns only the
/// opaque reference used by <see cref="AiProviderAuthentication.OAuth"/>.
/// </summary>
public sealed class AiProviderAuthenticationRuntime : IAiProviderAuthenticationRuntime
{
    private static readonly Uri OpenAiAuthorizeEndpoint =
        new("https://auth.openai.com/oauth/authorize");
    private static readonly Uri OpenAiTokenEndpoint =
        new("https://auth.openai.com/oauth/token");
    private static readonly Uri OpenAiDeviceUserCodeEndpoint =
        new("https://auth.openai.com/api/accounts/deviceauth/usercode");
    private static readonly Uri OpenAiDeviceTokenEndpoint =
        new("https://auth.openai.com/api/accounts/deviceauth/token");
    private static readonly Uri OpenAiDeviceVerificationEndpoint =
        new("https://auth.openai.com/codex/device");
    private static readonly Uri OpenAiDeviceRedirectEndpoint =
        new("https://auth.openai.com/deviceauth/callback");

    // OpenAI's public Codex client registers this literal redirect. A random
    // port, 127.0.0.1, or a trailing slash is a different OAuth redirect URI.
    private static readonly Uri OpenAiBrowserRedirectEndpoint =
        new("http://localhost:1455/auth/callback");
    private static readonly Uri GitHubDeviceCodeEndpoint =
        new("https://github.com/login/device/code");
    private static readonly Uri GitHubAccessTokenEndpoint =
        new("https://github.com/login/oauth/access_token");
    private const string OpenAiScope = "openid profile email offline_access";
    private const string GitHubScope = "read:user";
    private const int PollingSafetyMarginSeconds = 3;

    private readonly AiProviderOAuthOptions _options;
    private readonly AiProviderOAuthHttp _http;
    private readonly AiProviderOAuthVault _vault;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public AiProviderAuthenticationRuntime(
        ISecretVault secretVault,
        AiProviderOAuthOptions? options = null)
        : this(
            secretVault,
            options,
            handler: null,
            TimeProvider.System,
            delay: null)
    {
    }

    internal AiProviderAuthenticationRuntime(
        ISecretVault secretVault,
        AiProviderOAuthOptions? options,
        HttpMessageHandler? handler,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(secretVault);
        _options = options ?? new AiProviderOAuthOptions();
        _http = new AiProviderOAuthHttp(handler);
        _vault = new AiProviderOAuthVault(secretVault);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _delay = delay ?? ((duration, cancellationToken) =>
            Task.Delay(duration, _timeProvider, cancellationToken));
    }

    public AiProviderAuthenticationAvailability GetAvailability(
        AiProviderKind provider,
        AiProviderOAuthFlow flow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (provider == AiProviderKind.OpenAi && Enum.IsDefined(flow))
        {
            return AiProviderAuthenticationAvailability.Available;
        }

        if (provider == AiProviderKind.GitHubCopilot
            && flow == AiProviderOAuthFlow.Device)
        {
            return AiProviderAuthenticationAvailability.Available;
        }

        return new AiProviderAuthenticationAvailability(
            false,
            "ai_provider_authentication_method_unavailable",
            "This provider does not support the selected authentication flow.");
    }

    public async ValueTask<AiProviderBrowserAuthorization> StartBrowserAsync(
        AiProviderProfileId profileId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var flowLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        flowLifetime.CancelAfter(_options.BrowserTimeout);
        var flowToken = flowLifetime.Token;
        try
        {
            var state = RandomBase64Url(32);
            // The public Codex client uses a 32-byte verifier. Although longer
            // values are legal PKCE, the provider contract is exact and both
            // reference implementations use this 43-character form.
            var verifier = RandomBase64Url(32);
            var challenge = Base64Url(SHA256.HashData(
                Encoding.ASCII.GetBytes(verifier)));
            var loopback = await StartLoopbackAsync(
                state,
                _options.BrowserTimeout,
                flowToken).ConfigureAwait(false);
            var authorizationUri = BuildOpenAiAuthorizeUri(
                loopback.RedirectUri,
                state,
                challenge);
            var completion = CompleteBrowserAsync(
                profileId,
                loopback,
                verifier,
                flowToken);
            return new AiProviderBrowserAuthorization(
                authorizationUri,
                DisposeFlowAsync(completion, flowLifetime));
        }
        catch
        {
            flowLifetime.Dispose();
            throw;
        }
    }

    public async ValueTask<AiProviderDeviceAuthorization> StartDeviceAsync(
        AiProviderProfileId profileId,
        AiProviderKind provider,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var flowLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        flowLifetime.CancelAfter(_options.DeviceTimeout);
        try
        {
            var authorization = provider switch
            {
                AiProviderKind.OpenAi => await StartOpenAiDeviceAsync(
                    profileId,
                    flowLifetime.Token).ConfigureAwait(false),
                AiProviderKind.GitHubCopilot => await StartGitHubDeviceAsync(
                    profileId,
                    flowLifetime.Token).ConfigureAwait(false),
                _ => throw new ArgumentException(
                    "The provider does not support device authorization.",
                    nameof(provider)),
            };
            return authorization with
            {
                Completion = DisposeFlowAsync(
                    authorization.Completion,
                    flowLifetime),
            };
        }
        catch
        {
            flowLifetime.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _http.Dispose();
        _lifetime.Dispose();
    }

    private async ValueTask<AiProviderDeviceAuthorization> StartOpenAiDeviceAsync(
        AiProviderProfileId profileId,
        CancellationToken cancellationToken)
    {
        var request = AiProviderJson.Write(
            4 * 1024,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("client_id", _options.OpenAiClientId);
                writer.WriteEndObject();
            });
        var body = await _http.PostJsonAsync(
            OpenAiDeviceUserCodeEndpoint,
            request,
            cancellationToken).ConfigureAwait(false);
        using var document = AiProviderJson.Parse(body);
        var root = document.RootElement;
        var deviceAuthId = RequiredString(root, "device_auth_id", 2048);
        var userCode = RequiredString(root, "user_code", 256);
        var interval = ReadSeconds(root, "interval", defaultValue: 5);
        var now = _timeProvider.GetUtcNow();
        var expiresAt = AiProviderOAuthExpiry.Read(
            root,
            now,
            ceiling: now.Add(_options.DeviceTimeout));
        var sessionReference = SecretRef.New();
        var completion = PollOpenAiDeviceAsync(
            profileId,
            sessionReference,
            deviceAuthId,
            userCode,
            interval,
            expiresAt,
            cancellationToken);
        return new AiProviderDeviceAuthorization(
            OpenAiDeviceVerificationEndpoint,
            userCode,
            TimeSpan.FromSeconds(interval),
            expiresAt,
            completion);
    }

    private async ValueTask<AiProviderDeviceAuthorization> StartGitHubDeviceAsync(
        AiProviderProfileId profileId,
        CancellationToken cancellationToken)
    {
        var clientId = _options.GitHubClientId;
        var body = await _http.PostFormAsync(
            GitHubDeviceCodeEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = clientId,
                ["scope"] = GitHubScope,
            },
            cancellationToken).ConfigureAwait(false);
        using var document = AiProviderJson.Parse(body);
        var root = document.RootElement;
        var deviceCode = RequiredString(root, "device_code", 2048);
        var userCode = RequiredString(root, "user_code", 256);
        var verificationUri = RequiredHttpsUri(root, "verification_uri");
        var interval = ReadSeconds(root, "interval", defaultValue: 5);
        var expiresIn = ReadSeconds(
            root,
            "expires_in",
            defaultValue: checked((int)_options.DeviceTimeout.TotalSeconds));
        var now = _timeProvider.GetUtcNow();
        var expiresAt = Earliest(
            now.AddSeconds(expiresIn),
            now.Add(_options.DeviceTimeout));
        var sessionReference = SecretRef.New();
        var completion = PollGitHubDeviceAsync(
            profileId,
            sessionReference,
            clientId,
            deviceCode,
            interval,
            expiresAt,
            cancellationToken);
        return new AiProviderDeviceAuthorization(
            verificationUri,
            userCode,
            TimeSpan.FromSeconds(interval),
            expiresAt,
            completion);
    }

    private async Task<AiProviderAuthenticationResult> CompleteBrowserAsync(
        AiProviderProfileId profileId,
        LoopbackAuthorization loopback,
        string verifier,
        CancellationToken cancellationToken)
    {
        await using (loopback)
        {
            LoopbackCallback? callback = null;
            try
            {
                callback = await loopback.Callback.ConfigureAwait(false);
                var sessionReference = SecretRef.New();
                var result = await ExchangeAndStoreOpenAiSessionAsync(
                    profileId,
                    sessionReference,
                    callback.Code,
                    loopback.RedirectUri,
                    verifier,
                    cancellationToken).ConfigureAwait(false);
                await callback.RespondAsync(
                    result.Succeeded,
                    result.Succeeded ? null : result.Message).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException)
            {
                await TryRespondAsync(callback, succeeded: false).ConfigureAwait(false);
                return AiProviderAuthenticationResult.Failure(
                    "ai_provider_authentication_cancelled",
                    "Authentication was cancelled.");
            }
            catch (Exception exception) when (exception is AiProviderClientException
                or JsonException
                or InvalidOperationException)
            {
                await TryRespondAsync(callback, succeeded: false).ConfigureAwait(false);
                return AiProviderAuthenticationResult.Failure(
                    "ai_provider_authentication_failed",
                    "Authentication failed.");
            }
        }
    }

    private async Task<AiProviderAuthenticationResult> PollOpenAiDeviceAsync(
        AiProviderProfileId profileId,
        SecretRef sessionReference,
        string deviceAuthId,
        string userCode,
        int intervalSeconds,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var interval = intervalSeconds;
            while (_timeProvider.GetUtcNow() < expiresAt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = AiProviderJson.Write(
                    8 * 1024,
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("device_auth_id", deviceAuthId);
                        writer.WriteString("user_code", userCode);
                        writer.WriteEndObject();
                    });
                var response = await _http.PostJsonResponseAsync(
                    OpenAiDeviceTokenEndpoint,
                    request,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden
                    or HttpStatusCode.NotFound)
                {
                    await DelayForPollingAsync(interval, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var error = ReadDeviceError(response.Body);
                    if (string.Equals(error, "deviceauth_authorization_pending", StringComparison.Ordinal))
                    {
                        await DelayForPollingAsync(interval, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (string.Equals(error, "slow_down", StringComparison.Ordinal))
                    {
                        interval = checked(interval + 5);
                        await DelayForPollingAsync(interval, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    return AiProviderAuthenticationResult.Failure(
string.Equals(error, "access_denied"
, StringComparison.Ordinal) ? "ai_provider_authentication_denied"
                            : "ai_provider_authentication_failed",
string.Equals(error, "access_denied"
, StringComparison.Ordinal) ? "Authentication was denied."
                            : "Authentication failed.");
                }

                using var document = AiProviderJson.Parse(response.Body);
                var authorizationCode = RequiredString(
                    document.RootElement,
                    "authorization_code",
                    16 * 1024);
                var verifier = RequiredString(
                    document.RootElement,
                    "code_verifier",
                    16 * 1024);
                return await ExchangeAndStoreOpenAiSessionAsync(
                    profileId,
                    sessionReference,
                    authorizationCode,
                    OpenAiDeviceRedirectEndpoint,
                    verifier,
                    cancellationToken).ConfigureAwait(false);
            }

            return AiProviderAuthenticationResult.Failure(
                "ai_provider_device_code_expired",
                "The device code expired.");
        }
        catch (OperationCanceledException)
        {
            return AiProviderAuthenticationResult.Failure(
                "ai_provider_authentication_cancelled",
                "Authentication was cancelled.");
        }
        catch (AiProviderClientException)
        {
            return AiProviderAuthenticationResult.Failure(
                "ai_provider_authentication_failed",
                "Authentication failed.");
        }
    }

    private async Task<AiProviderAuthenticationResult> PollGitHubDeviceAsync(
        AiProviderProfileId profileId,
        SecretRef sessionReference,
        string clientId,
        string deviceCode,
        int intervalSeconds,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var interval = intervalSeconds;
            while (_timeProvider.GetUtcNow() < expiresAt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = await _http.PostFormAsync(
                    GitHubAccessTokenEndpoint,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["client_id"] = clientId,
                        ["device_code"] = deviceCode,
                        ["grant_type"] =
                            "urn:ietf:params:oauth:grant-type:device_code",
                    },
                    cancellationToken).ConfigureAwait(false);
                using var document = AiProviderJson.Parse(body);
                var root = document.RootElement;
                var accessToken = AiProviderJson.OptionalBoundedString(
                    root,
                    "access_token",
                    64 * 1024);
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    var session = await GitHubCopilotOAuth.ExchangeAsync(
                        accessToken,
                        _http,
                        _timeProvider,
                        cancellationToken).ConfigureAwait(false);
                    await _vault.StoreAsync(
                        profileId,
                        sessionReference,
                        session,
                        cancellationToken).ConfigureAwait(false);
                    return AiProviderAuthenticationResult.Success(sessionReference);
                }

                var error = AiProviderJson.OptionalBoundedString(root, "error", 128);
                switch (error)
                {
                    case "authorization_pending":
                        break;
                    case "slow_down":
                        interval = checked(interval + 5);
                        break;
                    case "expired_token":
                        return AiProviderAuthenticationResult.Failure(
                            "ai_provider_device_code_expired",
                            "The device code expired.");
                    case "access_denied":
                        return AiProviderAuthenticationResult.Failure(
                            "ai_provider_authentication_denied",
                            "Authentication was denied.");
                    default:
                        return AiProviderAuthenticationResult.Failure(
                            "ai_provider_authentication_failed",
                            "Authentication failed.");
                }

                await DelayForPollingAsync(interval, cancellationToken).ConfigureAwait(false);
            }

            return AiProviderAuthenticationResult.Failure(
                "ai_provider_device_code_expired",
                "The device code expired.");
        }
        catch (OperationCanceledException)
        {
            return AiProviderAuthenticationResult.Failure(
                "ai_provider_authentication_cancelled",
                "Authentication was cancelled.");
        }
        catch (AiProviderClientException)
        {
            return AiProviderAuthenticationResult.Failure(
                "ai_provider_authentication_failed",
                "Authentication failed.");
        }
    }

    private async ValueTask<AiProviderOAuthSession> ExchangeOpenAiCodeAsync(
        string code,
        Uri redirectUri,
        string verifier,
        CancellationToken cancellationToken)
    {
        var response = await _http.PostFormResponseAsync(
            OpenAiTokenEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = _options.OpenAiClientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri.AbsoluteUri,
                ["code_verifier"] = verifier,
            },
            cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is < 200 or > 299)
        {
            throw OpenAiTokenExchangeException(response.StatusCode);
        }

        using var document = AiProviderJson.Parse(response.Body);
        return ParseOpenAiTokenResponse(document.RootElement, existingRefreshToken: null);
    }

    private AiProviderOAuthSession ParseOpenAiTokenResponse(
        JsonElement root,
        string? existingRefreshToken)
    {
        string accessToken;
        try
        {
            accessToken = RequiredString(root, "access_token", 64 * 1024);
        }
        catch (AiProviderClientException)
        {
            throw new OpenAiTokenResponseException(OpenAiTokenResponseIssue.AccessToken);
        }

        var refreshToken = AiProviderJson.OptionalBoundedString(
                root,
                "refresh_token",
                64 * 1024)
            ?? existingRefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new OpenAiTokenResponseException(OpenAiTokenResponseIssue.RefreshToken);
        }

        var idToken = AiProviderJson.OptionalBoundedString(root, "id_token", 64 * 1024);
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset expiresAt;
        try
        {
            expiresAt = AiProviderOAuthExpiry.Read(root, now);
        }
        catch (AiProviderClientException)
        {
            throw new OpenAiTokenResponseException(OpenAiTokenResponseIssue.Expiry);
        }

        try
        {
            return new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "openai",
                accessToken,
                refreshToken,
                expiresAt,
                AiProviderOAuthClaims.ExtractAccountId(accessToken, idToken));
        }
        catch (ArgumentException)
        {
            throw new OpenAiTokenResponseException(OpenAiTokenResponseIssue.TokenValue);
        }
    }

    private async Task<AiProviderAuthenticationResult> ExchangeAndStoreOpenAiSessionAsync(
        AiProviderProfileId profileId,
        SecretRef sessionReference,
        string authorizationCode,
        Uri redirectUri,
        string verifier,
        CancellationToken cancellationToken)
    {
        AiProviderOAuthSession session;
        try
        {
            session = await ExchangeOpenAiCodeAsync(
                authorizationCode,
                redirectUri,
                verifier,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AiProviderClientException exception)
        {
            return OpenAiExchangeFailure(exception);
        }
        catch (OpenAiTokenResponseException exception)
        {
            return InvalidOpenAiExchangeResponse(exception.Issue);
        }
        catch (JsonException)
        {
            return InvalidOpenAiExchangeResponse();
        }
        catch (ArgumentException)
        {
            return InvalidOpenAiExchangeResponse();
        }

        try
        {
            await _vault.StoreAsync(
                profileId,
                sessionReference,
                session,
                cancellationToken).ConfigureAwait(false);
            return AiProviderAuthenticationResult.Success(sessionReference);
        }
        catch (AiProviderClientException exception)
            when (exception.Code == AiProviderRuntimeErrorCode.CredentialUnavailable)
        {
            return AiProviderAuthenticationResult.Failure(
                "ai_provider_oauth_session_store_failed",
                "The OAuth session could not be stored in the OS vault.");
        }
        catch (AiProviderClientException exception)
            when (exception.Code == AiProviderRuntimeErrorCode.Cancelled)
        {
            return AiProviderAuthenticationResult.Failure(
                "ai_provider_authentication_cancelled",
                "Authentication was cancelled.");
        }
    }

    private async Task DelayForPollingAsync(
        int intervalSeconds,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(
            checked(intervalSeconds + PollingSafetyMarginSeconds));
        await _delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private Uri BuildOpenAiAuthorizeUri(
        Uri redirectUri,
        string state,
        string challenge)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = _options.OpenAiClientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = OpenAiScope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "asura",
        };
        var builder = new UriBuilder(OpenAiAuthorizeEndpoint)
        {
            Query = string.Join(
                "&",
                query.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri;
    }

    private static async ValueTask<LoopbackAuthorization> StartLoopbackAsync(
        string expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add("http://localhost:1455/");
            listener.Start();
        }
        catch
        {
            listener.Close();
            throw;
        }

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeout);
        var callback = AwaitCallbackAsync(listener, expectedState, lifetime.Token);
        return new LoopbackAuthorization(
            listener,
            lifetime,
            OpenAiBrowserRedirectEndpoint,
            callback);
    }

    private static async Task<LoopbackCallback> AwaitCallbackAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var context = await listener.GetContextAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var request = context.Request;
            if (!string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal)
                || !string.Equals(request.Url?.AbsolutePath, OpenAiBrowserRedirectEndpoint.AbsolutePath
, StringComparison.Ordinal) || !IsLoopback(request.RemoteEndPoint?.Address))
            {
                await RespondAsync(context.Response, succeeded: false).ConfigureAwait(false);
                continue;
            }

            var query = request.QueryString;
            var state = query["state"];
            var code = query["code"];
            var error = query["error"];
            if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                await RespondAsync(context.Response, succeeded: false).ConfigureAwait(false);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
            {
                await RespondAsync(context.Response, succeeded: false).ConfigureAwait(false);
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.AuthenticationFailed);
            }

            return new LoopbackCallback(context.Response, code);
        }
    }

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);

    private static string? ReadDeviceError(byte[] body)
    {
        try
        {
            using var document = AiProviderJson.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var error))
            {
                return null;
            }

            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            return error.ValueKind == JsonValueKind.Object
                ? AiProviderJson.OptionalBoundedString(error, "code", 128)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RequiredString(
        JsonElement root,
        string propertyName,
        int maximumLength) =>
        AiProviderJson.RequiredBoundedString(root, propertyName, maximumLength);

    private static Uri RequiredHttpsUri(JsonElement root, string propertyName)
    {
        var value = RequiredString(root, propertyName, 2048);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps
, StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError);
        }

        return uri;
    }

    private static int ReadSeconds(
        JsonElement root,
        string propertyName,
        int defaultValue)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return defaultValue;
        }

        var parsed = property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number) => number,
            _ => 0d,
        };
        if (!double.IsFinite(parsed)
            || parsed <= 0
            || parsed > 24 * 60 * 60)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError);
        }

        return Math.Max(1, checked((int)Math.Floor(parsed)));
    }

    private static AiProviderAuthenticationResult OpenAiExchangeFailure(
        AiProviderClientException exception) => exception.Code switch
        {
            AiProviderRuntimeErrorCode.AuthenticationFailed
                or AiProviderRuntimeErrorCode.AccessDenied =>
                AiProviderAuthenticationResult.Failure(
                    "ai_provider_oauth_token_exchange_rejected",
                    "OpenAI rejected the OAuth token exchange. Start authentication again."),
            AiProviderRuntimeErrorCode.ProviderUnavailable
                or AiProviderRuntimeErrorCode.RateLimited =>
                AiProviderAuthenticationResult.Failure(
                    "ai_provider_oauth_token_exchange_unavailable",
                    "OpenAI's OAuth token exchange is temporarily unavailable."),
            _ => InvalidOpenAiExchangeResponse(),
        };

    private static AiProviderAuthenticationResult InvalidOpenAiExchangeResponse() =>
        AiProviderAuthenticationResult.Failure(
            "ai_provider_oauth_token_exchange_invalid_response",
            "OpenAI returned an invalid OAuth token response.");

    private static AiProviderAuthenticationResult InvalidOpenAiExchangeResponse(
        OpenAiTokenResponseIssue issue) => issue switch
        {
            OpenAiTokenResponseIssue.AccessToken => AiProviderAuthenticationResult.Failure(
                "ai_provider_oauth_token_response_missing_access_token",
                "OpenAI's OAuth response did not include a valid access token."),
            OpenAiTokenResponseIssue.RefreshToken => AiProviderAuthenticationResult.Failure(
                "ai_provider_oauth_token_response_missing_refresh_token",
                "OpenAI's OAuth response did not include a refresh token."),
            OpenAiTokenResponseIssue.Expiry => AiProviderAuthenticationResult.Failure(
                "ai_provider_oauth_token_response_invalid_expiry",
                "OpenAI's OAuth response included an invalid token expiry."),
            _ => InvalidOpenAiExchangeResponse(),
        };

    private static AiProviderClientException OpenAiTokenExchangeException(
        HttpStatusCode statusCode) => statusCode switch
        {
            HttpStatusCode.BadRequest
                or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden => AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.AuthenticationFailed),
            HttpStatusCode.TooManyRequests => AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.RateLimited),
            >= HttpStatusCode.InternalServerError => AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProviderUnavailable),
            _ => AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError),
        };

    private static DateTimeOffset Earliest(
        DateTimeOffset first,
        DateTimeOffset second) => first <= second ? first : second;

    private static string RandomBase64Url(int byteCount) =>
        Base64Url(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static async Task RespondAsync(
        HttpListenerResponse response,
        bool succeeded,
        string? failureMessage = null)
    {
        var body = Encoding.UTF8.GetBytes(
            succeeded
                ? "Authentication complete. Return to Asura."
                : $"{failureMessage ?? "Authentication failed."} Return to Asura.");
        response.StatusCode = succeeded ? 200 : 400;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        response.Close();
    }

    private static async Task TryRespondAsync(
        LoopbackCallback? callback,
        bool succeeded)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            await callback.RespondAsync(succeeded, failureMessage: null).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpListenerException
            or IOException
            or ObjectDisposedException)
        {
            // The browser may close the loopback request while the token
            // exchange is being cancelled. Authentication state is already
            // represented by the returned typed result.
        }
    }

    private static async Task<AiProviderAuthenticationResult> DisposeFlowAsync(
        Task<AiProviderAuthenticationResult> completion,
        CancellationTokenSource lifetime)
    {
        try
        {
            return await completion.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private sealed class LoopbackAuthorization(
        HttpListener listener,
        CancellationTokenSource lifetime,
        Uri redirectUri,
        Task<LoopbackCallback> callback) : IAsyncDisposable
    {
        public Uri RedirectUri { get; } = redirectUri;

        public Task<LoopbackCallback> Callback { get; } = callback;

        public ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            listener.Close();
            lifetime.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record LoopbackCallback(
        HttpListenerResponse Response,
        string Code)
    {
        public Task RespondAsync(bool succeeded, string? failureMessage) =>
            AiProviderAuthenticationRuntime.RespondAsync(
                Response,
                succeeded,
                failureMessage);
    }

    private enum OpenAiTokenResponseIssue
    {
        AccessToken,
        RefreshToken,
        Expiry,
        TokenValue,
    }

    private sealed class OpenAiTokenResponseException(OpenAiTokenResponseIssue issue)
        : Exception
    {
        public OpenAiTokenResponseIssue Issue { get; } = issue;

    }
}
