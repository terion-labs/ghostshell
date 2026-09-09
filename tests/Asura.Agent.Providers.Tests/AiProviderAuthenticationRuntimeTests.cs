using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;
using Asura.Infrastructure;

namespace Asura.Agent.Providers.Tests;

public sealed class AiProviderAuthenticationRuntimeTests
{
    private static readonly AiProviderProfileId ProfileId =
        new("interactive-auth-profile");

    [Fact]
    public async Task GitHubDeviceAuthorizationUsesFirstPartyCopilotClientByDefault()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(
            JsonResponse(
                "{\"device_code\":\"device-secret\",\"user_code\":\"ABCD\","
                + "\"verification_uri\":\"https://github.com/login/device\","
                + "\"expires_in\":900,\"interval\":1}"),
            JsonResponse("{\"access_token\":\"github-access-secret\"}"),
            CopilotTokenResponse("copilot-access-secret"));
        using var runtime = CreateRuntime(vault, handler, gitHubClientId: null);

        var availability = runtime.GetAvailability(
            AiProviderKind.GitHubCopilot,
            AiProviderOAuthFlow.Device);

        var authorization = await runtime.StartDeviceAsync(
            ProfileId,
            AiProviderKind.GitHubCopilot,
            CancellationToken.None);
        var result = await authorization.Completion;

        Assert.True(availability.IsAvailable);
        Assert.True(result.Succeeded);
        Assert.Contains(
            "client_id=" + AiProviderOAuthOptions.GitHubCopilotDefaultClientId,
            handler.Requests[0].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubDeviceAuthorizationPollsAndStoresOnlyAnOpaqueSessionReference()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(
            JsonResponse(
                "{\"device_code\":\"device-secret\",\"user_code\":\"ABCD-EFGH\","
                + "\"verification_uri\":\"https://github.com/login/device\","
                + "\"expires_in\":900,\"interval\":5}"),
            JsonResponse("{\"error\":\"authorization_pending\"}"),
            JsonResponse("{\"error\":\"slow_down\"}"),
            JsonResponse("{\"access_token\":\"github-access-secret\",\"token_type\":\"bearer\"}"),
            CopilotTokenResponse("copilot-access-secret"));
        var delays = new List<TimeSpan>();
        using var runtime = CreateRuntime(
            vault,
            handler,
            gitHubClientId: "asura-client-id",
            delay: (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        var authorization = await runtime.StartDeviceAsync(
            ProfileId,
            AiProviderKind.GitHubCopilot,
            CancellationToken.None);
        var result = await authorization.Completion;

        Assert.True(result.Succeeded);
        Assert.Equal("ABCD-EFGH", authorization.UserCode);
        Assert.Equal(new Uri("https://github.com/login/device"), authorization.VerificationUri);
        Assert.NotNull(result.Session);
        Assert.DoesNotContain("github-access-secret", JsonSerializer.Serialize(result));
        Assert.Equal([TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(13)], delays);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Contains("client_id=asura-client-id", handler.Requests[0].Body);
        Assert.DoesNotContain(
            "app_EMoamEEZ73f0CkXaXp7hrann",
            handler.Requests[0].Body,
            StringComparison.Ordinal);
        Assert.Equal(
            "https://api.github.com/copilot_internal/v2/token",
            handler.Requests[^1].Uri.AbsoluteUri);
        Assert.Equal("Bearer github-access-secret", handler.Requests[^1].Authorization);
        Assert.Equal(
            "vscode/1.107.0",
            handler.Requests[^1].Headers["Editor-Version"]);

        var session = await ResolveSessionAsync(vault, result.Session.Value);
        Assert.Equal("github-copilot", session.Provider);
        Assert.Equal("copilot-access-secret", session.AccessToken);
        Assert.Equal("github-access-secret", session.RefreshToken);
        Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task GitHubOAuthSessionNeverFollowsAnEditableProfileEndpoint()
    {
        using var vault = new InMemorySecretVault();
        var reference = new SecretRef("github-pinned-session");
        await new AiProviderOAuthVault(vault).StoreAsync(
            ProfileId,
            reference,
            new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "github-copilot",
                "github-access-secret",
                refreshToken: null,
                expiresAt: DateTimeOffset.MaxValue),
            CancellationToken.None);
        using var handler = new OAuthHandler(JsonResponse(
            "{\"data\":[{\"id\":\"gpt-5.3-codex\"}]}"));
        using var factory = new AiProviderFactory(vault, handler);
        var profile = new AiProviderProfile(
            ProfileId,
            AiProviderProfile.CurrentSchemaVersion,
            "Copilot",
            AiProviderKind.GitHubCopilot,
            new Uri("https://attacker.example.test/steal/"),
            new AiProviderAuthentication.OAuth(reference, AiProviderOAuthFlow.Device),
            "gpt-5.3-codex",
            order: 0);

        var models = await factory.ListModelsAsync(profile, CancellationToken.None);

        Assert.Equal("gpt-5.3-codex", Assert.Single(models).Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.githubcopilot.com/models", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer github-access-secret", request.Authorization);
        Assert.DoesNotContain("attacker.example.test", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task OpenAiOAuthDiscoversTheSignedInAccountsVisibleCodexModels()
    {
        using var vault = new InMemorySecretVault();
        var reference = new SecretRef("openai-model-session");
        await new AiProviderOAuthVault(vault).StoreAsync(
            ProfileId,
            reference,
            new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "openai",
                "openai-access-secret",
                refreshToken: null,
                expiresAt: DateTimeOffset.MaxValue,
                accountId: "account-123"),
            CancellationToken.None);
        using var handler = new OAuthHandler(JsonResponse(
            """{"models":[{"slug":"gpt-visible","display_name":"Visible","visibility":"list"},{"slug":"gpt-hidden","display_name":"Hidden","visibility":"hide"}]}"""));
        using var factory = new AiProviderFactory(vault, handler);
        var profile = new AiProviderProfile(
            ProfileId,
            AiProviderProfile.CurrentSchemaVersion,
            "OpenAI",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.OAuth(reference, AiProviderOAuthFlow.Browser),
            "gpt-visible",
            order: 0);

        var models = await factory.ListOpenAiCodexModelsAsync(
            profile,
            CancellationToken.None);

        Assert.Equal("gpt-visible", Assert.Single(models).Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://chatgpt.com/backend-api/codex/models?client_version=0.145.0",
            request.Uri.AbsoluteUri);
        Assert.Equal("Bearer openai-access-secret", request.Authorization);
        Assert.Equal("account-123", request.Headers["ChatGPT-Account-Id"]);
    }

    [Fact]
    public async Task DeviceAuthorizationCancellationStopsPollingWithoutWritingASecret()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(
            JsonResponse(
                "{\"device_code\":\"device-secret\",\"user_code\":\"ABCD\","
                + "\"verification_uri\":\"https://github.com/login/device\","
                + "\"expires_in\":900,\"interval\":1}"),
            JsonResponse("{\"error\":\"authorization_pending\"}"));
        using var cancellation = new CancellationTokenSource();
        var enteredDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = CreateRuntime(
            vault,
            handler,
            gitHubClientId: "asura-client-id",
            delay: async (_, token) =>
            {
                enteredDelay.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        var authorization = await runtime.StartDeviceAsync(
            ProfileId,
            AiProviderKind.GitHubCopilot,
            cancellation.Token);
        await enteredDelay.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await authorization.Completion;

        Assert.False(result.Succeeded);
        Assert.Equal("ai_provider_authentication_cancelled", result.StableCode);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task DeviceAuthorizationDeadlineCancelsAStalledPollingBody()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(
            JsonResponse(
                "{\"device_code\":\"device-secret\",\"user_code\":\"ABCD\","
                + "\"verification_uri\":\"https://github.com/login/device\","
                + "\"expires_in\":900,\"interval\":1}"),
            StalledJsonResponse());
        using var runtime = CreateRuntime(
            vault,
            handler,
            gitHubClientId: "asura-client-id",
            deviceTimeout: TimeSpan.FromMilliseconds(200));

        var authorization = await runtime.StartDeviceAsync(
            ProfileId,
            AiProviderKind.GitHubCopilot,
            CancellationToken.None);
        var result = await authorization.Completion.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(result.Succeeded);
        Assert.Equal("ai_provider_authentication_cancelled", result.StableCode);
        Assert.Null(result.Session);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DeviceAuthorizationExpiresAfterBoundedPollingWindow()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(
            JsonResponse(
                "{\"device_code\":\"device-secret\",\"user_code\":\"ABCD\","
                + "\"verification_uri\":\"https://github.com/login/device\","
                + "\"expires_in\":1,\"interval\":1}"),
            JsonResponse("{\"error\":\"authorization_pending\"}"));
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var runtime = CreateRuntime(
            vault,
            handler,
            gitHubClientId: "asura-client-id",
            timeProvider: time,
            delay: (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            });

        var authorization = await runtime.StartDeviceAsync(
            ProfileId,
            AiProviderKind.GitHubCopilot,
            CancellationToken.None);
        var result = await authorization.Completion;

        Assert.False(result.Succeeded);
        Assert.Equal("ai_provider_device_code_expired", result.StableCode);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task OpenAiDeviceAuthorizationExchangesCodeAndOwnsRefreshTokenInVault()
    {
        var now = new DateTimeOffset(2026, 8, 13, 19, 0, 0, TimeSpan.Zero);
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(
            JsonResponse(
                "{\"device_auth_id\":\"device-auth\",\"user_code\":\"OPEN-AI\","
                + "\"expires_at\":\"2026-08-13T19:15:00.0000000+00:00\",\"interval\":1}"),
            JsonResponse("{\"authorization_code\":\"authorization-code\","
                + "\"code_verifier\":\"device-verifier\"}"),
            JsonResponse("{\"access_token\":\"openai-access-secret\","
                + "\"refresh_token\":\"openai-refresh-secret\","
                + "\"expires_at\":\"2026-08-23T19:00:00.0000000+00:00\"}"));
        using var runtime = CreateRuntime(
            vault,
            handler,
            timeProvider: new ManualTimeProvider(now),
            delay: (_, _) => Task.CompletedTask);

        var authorization = await runtime.StartDeviceAsync(
            ProfileId,
            AiProviderKind.OpenAi,
            CancellationToken.None);
        var result = await authorization.Completion;

        Assert.True(result.Succeeded);
        Assert.Equal("OPEN-AI", authorization.UserCode);
        Assert.Equal(new Uri("https://auth.openai.com/codex/device"), authorization.VerificationUri);
        var session = await ResolveSessionAsync(vault, result.Session!.Value);
        Assert.Equal("openai", session.Provider);
        Assert.Equal("openai-access-secret", session.AccessToken);
        Assert.Equal("openai-refresh-secret", session.RefreshToken);
        Assert.Equal(now.AddDays(10), session.ExpiresAt);
        Assert.Equal(now.AddMinutes(15), authorization.ExpiresAt);
        Assert.Equal("https://auth.openai.com/oauth/token", handler.Requests[^1].Uri.AbsoluteUri);
        Assert.Contains("code_verifier=device-verifier", handler.Requests[^1].Body);
    }

    [Fact]
    public async Task OpenAiDeviceAuthorizationAcceptsStringTimingFieldsAndPendingStatuses()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(
            JsonResponse(
                "{\"device_auth_id\":\"device-auth\",\"user_code\":\"OPEN-AI\","
                + "\"expires_in\":\"900\",\"interval\":\"1\"}"),
            JsonResponse("{\"error\":\"not-ready\"}", HttpStatusCode.Forbidden),
            JsonResponse("{\"error\":\"not-ready\"}", HttpStatusCode.NotFound),
            JsonResponse(
                "{\"error\":{\"code\":\"deviceauth_authorization_pending\"}}",
                HttpStatusCode.BadRequest),
            JsonResponse("{\"error\":\"slow_down\"}", HttpStatusCode.BadRequest),
            JsonResponse("{\"authorization_code\":\"authorization-code\","
                + "\"code_verifier\":\"device-verifier\"}"),
            JsonResponse("{\"access_token\":\"openai-access-secret\","
                + "\"refresh_token\":\"openai-refresh-secret\","
                + "\"expires_in\":\"3599.75\"}"));
        var delays = new List<TimeSpan>();
        using var runtime = CreateRuntime(
            vault,
            handler,
            delay: (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        var authorization = await runtime.StartDeviceAsync(
            ProfileId,
            AiProviderKind.OpenAi,
            CancellationToken.None);
        var result = await authorization.Completion;

        Assert.True(result.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(1), authorization.PollInterval);
        Assert.Equal(
            [
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(9),
            ],
            delays);
    }

    [Fact]
    public async Task OpenAiBrowserAuthorizationUsesPkceAndValidatesLoopbackState()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(
            JsonResponse("{\"access_token\":\"browser-access-secret\","
                + "\"refresh_token\":\"browser-refresh-secret\","
                + "\"expires_in\":\"864000.0\"}"));
        using var runtime = CreateRuntime(vault, handler);

        var authorization = await runtime.StartBrowserAsync(
            ProfileId,
            CancellationToken.None);
        var query = ParseQuery(authorization.AuthorizationUri.Query);
        Assert.Equal("http://localhost:1455/auth/callback", query["redirect_uri"]);
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", query["client_id"]);
        Assert.Equal("openid profile email offline_access", query["scope"]);
        Assert.Equal("true", query["id_token_add_organizations"]);
        Assert.Equal("true", query["codex_cli_simplified_flow"]);
        Assert.Equal("asura", query["originator"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.False(string.IsNullOrWhiteSpace(query["state"]));
        var redirectUri = new Uri(query["redirect_uri"]);
        var invalidCallback = new UriBuilder(redirectUri)
        {
            Query = "code=attacker-code&state=wrong-state",
        }.Uri;
        using var browser = new HttpClient();
        using var invalidResponse = await browser.GetAsync(invalidCallback);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        var callback = new UriBuilder(redirectUri)
        {
            Query = "code=browser-code&state=" + Uri.EscapeDataString(query["state"]),
        }.Uri;
        using var callbackResponse = await browser.GetAsync(callback);
        var result = await authorization.Completion;

        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
        Assert.True(result.Succeeded);
        Assert.Contains("code=browser-code", handler.Requests[0].Body);
        Assert.Contains("code_verifier=", handler.Requests[0].Body);
        var exchange = ParseQuery(handler.Requests[0].Body);
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal(query["client_id"], exchange["client_id"]);
        Assert.Equal(query["redirect_uri"], exchange["redirect_uri"]);
        Assert.Equal(43, exchange["code_verifier"].Length);
        Assert.DoesNotContain(
            query["code_challenge"],
            handler.Requests[0].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiDeviceTokenExchangeRejectionReturnsAnActionableStableFailure()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(
            JsonResponse(
                "{\"device_auth_id\":\"device-auth\",\"user_code\":\"OPEN-AI\","
                + "\"expires_in\":900,\"interval\":1}"),
            JsonResponse("{\"authorization_code\":\"authorization-code\","
                + "\"code_verifier\":\"device-verifier\"}"),
            JsonResponse(
                "{\"error\":{\"code\":\"token_expired\"}}",
                HttpStatusCode.BadRequest));
        using var runtime = CreateRuntime(vault, handler, delay: (_, _) => Task.CompletedTask);

        var authorization = await runtime.StartDeviceAsync(
            ProfileId,
            AiProviderKind.OpenAi,
            CancellationToken.None);
        var result = await authorization.Completion;

        Assert.False(result.Succeeded);
        Assert.Equal("ai_provider_oauth_token_exchange_rejected", result.StableCode);
        Assert.Equal(
            "OpenAI rejected the OAuth token exchange. Start authentication again.",
            result.Message);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task BrowserAuthorizationDeadlineCancelsAStalledTokenExchangeBody()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(StalledJsonResponse());
        using var runtime = CreateRuntime(
            vault,
            handler,
            browserTimeout: TimeSpan.FromMilliseconds(200));

        var authorization = await runtime.StartBrowserAsync(
            ProfileId,
            CancellationToken.None);
        var query = ParseQuery(authorization.AuthorizationUri.Query);
        var callback = new UriBuilder(new Uri(query["redirect_uri"]))
        {
            Query = "code=browser-code&state=" + Uri.EscapeDataString(query["state"]),
        }.Uri;
        using var browser = new HttpClient();
        var callbackResponse = browser.GetAsync(callback);

        var result = await authorization.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        using var response = await callbackResponse.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(result.Succeeded);
        Assert.Equal("ai_provider_authentication_cancelled", result.StableCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BrowserAuthorizationReportsTheInvalidTokenFieldWithoutExposingPayloads()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new OAuthHandler(
            JsonResponse("{\"access_token\":\"browser-access-secret\","
                + "\"expires_in\":3600}"));
        using var runtime = CreateRuntime(vault, handler);

        var authorization = await runtime.StartBrowserAsync(
            ProfileId,
            CancellationToken.None);
        var query = ParseQuery(authorization.AuthorizationUri.Query);
        var callback = new UriBuilder(new Uri(query["redirect_uri"]))
        {
            Query = "code=browser-code&state=" + Uri.EscapeDataString(query["state"]),
        }.Uri;
        using var browser = new HttpClient();
        using var callbackResponse = await browser.GetAsync(callback);
        var callbackBody = await callbackResponse.Content.ReadAsStringAsync();
        var result = await authorization.Completion;

        Assert.False(result.Succeeded);
        Assert.Equal(
            "ai_provider_oauth_token_response_missing_refresh_token",
            result.StableCode);
        Assert.Equal(HttpStatusCode.BadRequest, callbackResponse.StatusCode);
        Assert.Contains("did not include a refresh token", callbackBody);
        Assert.DoesNotContain("browser-access-secret", callbackBody);
    }

    [Fact]
    public async Task ExpiredOpenAiSessionRefreshesRequestLocallyAndRoutesToCodexResponses()
    {
        using var vault = new InMemorySecretVault();
        var reference = new SecretRef("refreshable-openai-session");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var sessionVault = new AiProviderOAuthVault(vault);
        await sessionVault.StoreAsync(
            ProfileId,
            reference,
            new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "openai",
                "expired-access-secret",
                "refresh-secret",
                now.AddMinutes(-1),
                "old-account"),
            CancellationToken.None);
        using var handler = new OAuthHandler(
            JsonResponse("{\"access_token\":\"rotated-access-secret\","
                + "\"refresh_token\":\"rotated-refresh-secret\","
                + "\"expires_in\":\"864000.0\"}"),
            SseResponse(ResponsesTextStream("refreshed")));
        using var factory = new AiProviderFactory(
            vault,
            handler,
            limits: null,
            new AiProviderOAuthOptions(),
            time);
        var profile = new AiProviderProfile(
            ProfileId,
            AiProviderProfile.CurrentSchemaVersion,
            "OpenAI OAuth",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.OAuth(reference, AiProviderOAuthFlow.Device),
            "gpt-5.6-terra",
            order: 0);
        var nativeSession = new NativeAgentSession(new AgentRunId("oauth-refresh-run"));

        var result = await nativeSession.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://auth.openai.com/oauth/token", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(
            "https://chatgpt.com/backend-api/codex/responses",
            handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal("Bearer rotated-access-secret", handler.Requests[1].Authorization);
        Assert.Equal("old-account", handler.Requests[1].Headers["ChatGPT-Account-Id"]);
        var rotated = await ResolveSessionAsync(vault, reference);
        Assert.Equal("rotated-access-secret", rotated.AccessToken);
        Assert.Equal("rotated-refresh-secret", rotated.RefreshToken);
        Assert.Equal(now.AddDays(10), rotated.ExpiresAt);
    }

    [Fact]
    public async Task ExpiredGitHubSessionExchangesVaultRefreshTokenBeforeProviderRequest()
    {
        using var vault = new InMemorySecretVault();
        var reference = new SecretRef("refreshable-github-session");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        await new AiProviderOAuthVault(vault).StoreAsync(
            ProfileId,
            reference,
            new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "github-copilot",
                "expired-copilot-secret",
                "github-refresh-secret",
                now.AddMinutes(-1)),
            CancellationToken.None);
        using var handler = new OAuthHandler(
            CopilotTokenResponse("rotated-copilot-secret", now.AddHours(1)),
            JsonResponse("{\"data\":[{\"id\":\"gpt-5.6-terra\"}]}"));
        using var factory = new AiProviderFactory(
            vault,
            handler,
            limits: null,
            new AiProviderOAuthOptions(),
            time);
        var profile = new AiProviderProfile(
            ProfileId,
            AiProviderProfile.CurrentSchemaVersion,
            "Copilot OAuth",
            AiProviderKind.GitHubCopilot,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.GitHubCopilot),
            new AiProviderAuthentication.OAuth(reference, AiProviderOAuthFlow.Device),
            "gpt-5.6-terra",
            order: 0);

        var models = await factory.ListModelsAsync(profile, CancellationToken.None);

        Assert.Equal("gpt-5.6-terra", Assert.Single(models).Id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "https://api.github.com/copilot_internal/v2/token",
            handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("Bearer github-refresh-secret", handler.Requests[0].Authorization);
        Assert.Equal("https://api.githubcopilot.com/models", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal("Bearer rotated-copilot-secret", handler.Requests[1].Authorization);
        Assert.Equal(
            "2026-06-01",
            handler.Requests[1].Headers["X-GitHub-Api-Version"]);
        var rotated = await ResolveSessionAsync(vault, reference);
        Assert.Equal("rotated-copilot-secret", rotated.AccessToken);
        Assert.Equal("github-refresh-secret", rotated.RefreshToken);
        Assert.Equal(now.AddHours(1), rotated.ExpiresAt);
    }

    private static AiProviderAuthenticationRuntime CreateRuntime(
        InMemorySecretVault vault,
        HttpMessageHandler handler,
        string? gitHubClientId = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? browserTimeout = null,
        TimeSpan? deviceTimeout = null) =>
        new(
            vault,
            new AiProviderOAuthOptions(
                gitHubClientId: gitHubClientId,
                browserTimeout: browserTimeout,
                deviceTimeout: deviceTimeout),
            handler,
            timeProvider ?? TimeProvider.System,
            delay);

    private static async Task<AiProviderOAuthSession> ResolveSessionAsync(
        InMemorySecretVault vault,
        SecretRef reference) =>
        await new AiProviderOAuthVault(vault).ResolveAsync(
            ProfileId,
            reference,
            CancellationToken.None);

    private static HttpResponseMessage JsonResponse(
        string value,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage CopilotTokenResponse(string token) =>
        CopilotTokenResponse(token, DateTimeOffset.UtcNow.AddHours(1));

    private static HttpResponseMessage CopilotTokenResponse(
        string token,
        DateTimeOffset expiresAt) =>
        JsonResponse(JsonSerializer.Serialize(new
        {
            token,
            expires_at = expiresAt.ToUnixTimeSeconds(),
        }));

    private static HttpResponseMessage StalledJsonResponse()
    {
        var content = new StreamContent(new StallingReadStream());
        content.Headers.ContentType = new("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private static HttpResponseMessage SseResponse(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "text/event-stream"),
        };

    private static string ResponsesTextStream(string value) =>
        "event: response.created\n"
        + "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp\",\"status\":\"in_progress\"}}\n\n"
        + "event: response.output_text.delta\n"
        + "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg\",\"delta\":"
        + JsonSerializer.Serialize(value)
        + "}\n\n"
        + "event: response.completed\n"
        + "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp\",\"status\":\"completed\"}}\n\n";

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair[1]),
                StringComparer.Ordinal);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class OAuthHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpResponseMessage> _responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(await CapturedRequest.CreateAsync(request, cancellationToken));
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No OAuth response was configured.");
            }

            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class StallingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string Body,
        string? Authorization,
        IReadOnlyDictionary<string, string> Headers)
    {
        public static async Task<CapturedRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(", ", header.Value),
                StringComparer.OrdinalIgnoreCase);
            return new CapturedRequest(
                request.RequestUri!,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString(),
                headers);
        }
    }
}
