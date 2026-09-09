using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Asura.Infrastructure;

namespace Asura.Agent.Providers.Tests;

public sealed class AiProviderRuntimeBoundaryTests
{
    private static readonly DateTimeOffset StoredAt =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Open_ai_model_discovery_uses_exact_uri_bearer_auth_and_vault_scope()
    {
        using var vault = new RecordingSecretVault();
        var reference = SecretRef.New();
        var profile = ApiKeyProfile(
            AiProviderKind.OpenAi,
            "provider-openai",
            reference);
        await StoreCredentialAsync(vault, profile.Id, reference, "openai-test-key");
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """
            {
              "data": [
                { "id": "z-model" },
                { "id": "a-model", "display_name": "Alpha" }
              ]
            }
            """));
        using var factory = new AiProviderFactory(vault, handler);

        var models = await factory.ListModelsAsync(profile, CancellationToken.None);

        Assert.Equal(
            new Uri("https://api.openai.com/v1/models"),
            handler.LastRequest!.Uri);
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal("application/json", handler.LastRequest.Accept);
        Assert.Equal("Bearer openai-test-key", handler.LastRequest.Authorization);
        Assert.Null(handler.LastRequest.ApiKey);
        Assert.Null(handler.LastRequest.AnthropicVersion);
        Assert.Equal(["a-model", "z-model"], models.Select(model => model.Id), StringComparer.Ordinal);

        var resolve = Assert.IsType<ResolveSecretRequest>(vault.LastResolveRequest);
        Assert.Equal(reference, resolve.Reference);
        Assert.Equal(SecretScopeKind.AiProvider, resolve.Scope.Kind);
        Assert.Equal(profile.Id.Value, resolve.Scope.OwnerId);
        Assert.Equal(SecretUseKind.AiProviderAuthentication, resolve.Purpose.Kind);
        Assert.Equal(profile.Id.Value, resolve.Purpose.TargetId);
    }

    [Fact]
    public async Task Anthropic_model_discovery_uses_exact_uri_and_required_headers()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        var profile = ApiKeyProfile(
            AiProviderKind.Anthropic,
            "provider-anthropic",
            reference);
        await StoreCredentialAsync(vault, profile.Id, reference, "anthropic-test-key");
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """
            {
              "data": [
                { "id": "claude-test", "display_name": "Claude Test" }
              ]
            }
            """));
        var limits = new AiProviderRuntimeLimits(maximumModels: 7);
        using var factory = new AiProviderFactory(vault, handler, limits);

        var models = await factory.ListModelsAsync(profile, CancellationToken.None);

        Assert.Equal(
            new Uri("https://api.anthropic.com/v1/models?limit=7"),
            handler.LastRequest!.Uri);
        Assert.Null(handler.LastRequest.Authorization);
        Assert.Equal("anthropic-test-key", handler.LastRequest.ApiKey);
        Assert.Equal("2023-06-01", handler.LastRequest.AnthropicVersion);
        var model = Assert.Single(models);
        Assert.Equal("claude-test", model.Id);
        Assert.Equal("Claude Test", model.DisplayName);
    }

    [Fact]
    public async Task Google_native_protocol_fails_closed_before_model_discovery()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        var profile = ApiKeyProfile(
            AiProviderKind.Google,
            "provider-google",
            reference);
        await StoreCredentialAsync(vault, profile.Id, reference, "google-test-key");
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """
            {
              "models": [
                {
                  "name": "models/gemini-test",
                  "displayName": "Gemini Test"
                }
              ]
            }
            """));
        var limits = new AiProviderRuntimeLimits(maximumModels: 9);
        using var factory = new AiProviderFactory(vault, handler, limits);

        var exception = await Assert.ThrowsAsync<AiProviderClientException>(async () =>
            await factory.ListModelsAsync(profile, CancellationToken.None));

        Assert.Equal(AiProviderRuntimeErrorCode.InvalidConfiguration, exception.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Reasoning_policy_is_narrowed_by_provider_model_and_selected_wire_route()
    {
        var anthropic = ApiKeyProfile(
            AiProviderKind.Anthropic,
            "provider-anthropic-policy",
            SecretRef.New());
        var openAi = ApiKeyProfile(
            AiProviderKind.OpenAi,
            "provider-openai-policy",
            SecretRef.New());
        var copilot = new AiProviderProfile(
            new AiProviderProfileId("provider-copilot-policy"),
            AiProviderProfile.CurrentSchemaVersion,
            "Copilot",
            AiProviderKind.GitHubCopilot,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.GitHubCopilot),
            new AiProviderAuthentication.OAuth(
                new SecretRef("copilot-session"),
                AiProviderOAuthFlow.Device),
            "gpt-5.3-codex",
            order: 0);
        var compatible = LoopbackProfile();

        Assert.Equal(
            [AgentReasoningEffort.Automatic],
            AiProviderReasoningPolicy.SupportedEfforts(
                anthropic,
                "claude-sonnet-4-5").ToArray());
        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Off,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
                AgentReasoningEffort.ExtraHigh,
            ],
            AiProviderReasoningPolicy.SupportedEfforts(
                anthropic,
                "claude-opus-4-6").ToArray());
        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Off,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
                AgentReasoningEffort.ExtraHigh,
                AgentReasoningEffort.Max,
            ],
            AiProviderReasoningPolicy.SupportedEfforts(
                anthropic,
                "claude-opus-4-7").ToArray());
        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
                AgentReasoningEffort.ExtraHigh,
                AgentReasoningEffort.Max,
            ],
            AiProviderReasoningPolicy.SupportedEfforts(
                anthropic,
                "claude-fable-5").ToArray());
        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Off,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
            ],
            AiProviderReasoningPolicy.SupportedEfforts(openAi).ToArray());
        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Off,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
                AgentReasoningEffort.ExtraHigh,
            ],
            AiProviderReasoningPolicy.SupportedEfforts(copilot).ToArray());
        Assert.Equal(
            [AgentReasoningEffort.Automatic],
            AiProviderReasoningPolicy.SupportedEfforts(
                copilot,
                "gpt-5.6-terra").ToArray());
        Assert.Equal(
            [AgentReasoningEffort.Automatic],
            AiProviderReasoningPolicy.SupportedEfforts(compatible).ToArray());
    }

    [Fact]
    public void Model_policy_projects_gpt56_reasoning_and_route_specific_service_tiers()
    {
        var openAi = ApiKeyProfile(
            AiProviderKind.OpenAi,
            "provider-openai-gpt56",
            SecretRef.New(),
            defaultModel: "gpt-5.6-terra");
        var openAiOAuth = new AiProviderProfile(
            new AiProviderProfileId("provider-openai-oauth-gpt56"),
            AiProviderProfile.CurrentSchemaVersion,
            "OpenAI OAuth",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.OAuth(
                new SecretRef("openai-oauth-session-gpt56"),
                AiProviderOAuthFlow.Browser),
            "gpt-5.6-terra",
            order: 0);
        var xai = ApiKeyProfile(
            AiProviderKind.XAi,
            "provider-xai-grok46",
            SecretRef.New(),
            defaultModel: "grok-4.6");

        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Off,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
                AgentReasoningEffort.ExtraHigh,
                AgentReasoningEffort.Max,
            ],
            AiProviderReasoningPolicy.SupportedEfforts(openAi).ToArray());
        Assert.Equal(
            [
                AgentServiceTier.Automatic,
                AgentServiceTier.Default,
                AgentServiceTier.Flex,
                AgentServiceTier.Priority,
            ],
            AiProviderServiceTierPolicy.SupportedTiers(
                openAi,
                openAi.DefaultModel).ToArray());
        Assert.Empty(AiProviderServiceTierPolicy.SupportedTiers(
            openAiOAuth,
            openAiOAuth.DefaultModel));
        Assert.Equal(
            [AgentServiceTier.Default, AgentServiceTier.Priority],
            AiProviderServiceTierPolicy.SupportedTiers(
                xai,
                xai.DefaultModel).ToArray());
    }

    [Fact]
    public async Task Loopback_provider_without_authentication_does_not_resolve_a_secret()
    {
        using var vault = new RecordingSecretVault();
        var profile = LoopbackProfile();
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"data":[{"id":"local-model"}]}"""));
        using var factory = new AiProviderFactory(vault, handler);

        var models = await factory.ListModelsAsync(profile, CancellationToken.None);

        Assert.Equal(
            new Uri("http://localhost:11434/v1/models"),
            handler.LastRequest!.Uri);
        Assert.Null(handler.LastRequest.Authorization);
        Assert.Null(handler.LastRequest.ApiKey);
        Assert.Null(vault.LastResolveRequest);
        Assert.Equal("local-model", Assert.Single(models).Id);
    }

    [Fact]
    public async Task Missing_credential_fails_before_sending_http()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        var profile = ApiKeyProfile(AiProviderKind.OpenAi, "provider-missing", reference);
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"data":[{"id":"unused"}]}"""));
        using var factory = new AiProviderFactory(vault, handler);

        var exception = await Assert.ThrowsAsync<AiProviderClientException>(async () =>
            await factory.ListModelsAsync(profile, CancellationToken.None));

        Assert.Equal(AiProviderRuntimeErrorCode.CredentialUnavailable, exception.Code);
        Assert.Equal("ai_provider_credential_unavailable", exception.StableCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task OpenAiOAuthTestDiscoversModelsFromTheAuthenticatedCodexCatalog()
    {
        using var vault = new InMemorySecretVault();
        var profileId = new AiProviderProfileId("provider-openai-oauth");
        var reference = new SecretRef("provider-openai-oauth-session");
        await new AiProviderOAuthVault(vault).StoreAsync(
            profileId,
            reference,
            new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "openai",
                "fresh-access-token",
                "refresh-token",
                DateTimeOffset.UtcNow.AddHours(1),
                "account-id"),
            CancellationToken.None);
        var profile = new AiProviderProfile(
            profileId,
            AiProviderProfile.CurrentSchemaVersion,
            "OpenAI OAuth",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.OAuth(reference, AiProviderOAuthFlow.Device),
            "gpt-5.6-terra",
            order: 0);
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"models":[{"slug":"gpt-5.6-terra","display_name":"GPT-5.6 Terra","visibility":"list"}]}"""));
        using var factory = new AiProviderFactory(vault, handler);
        using var runtime = new CatalogAiProviderRuntime(
            new FixedDefinitionCatalog(DefinitionCatalogSnapshot.Empty),
            factory);

        var result = await runtime.TestAsync(profile, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("ai_provider_test_succeeded", result.Code);
        Assert.Equal("gpt-5.6-terra", Assert.Single(result.Models).Id);
        Assert.Equal(
            new Uri("https://chatgpt.com/backend-api/codex/models?client_version=0.145.0"),
            handler.LastRequest!.Uri);
        Assert.Equal("Bearer fresh-access-token", handler.LastRequest.Authorization);
        Assert.Equal("account-id", handler.LastRequest.ChatGptAccountId);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task OpenAiOAuthTestFailsWhenVaultSessionIsMissing()
    {
        using var vault = new InMemorySecretVault();
        var profile = new AiProviderProfile(
            new AiProviderProfileId("provider-openai-oauth-missing"),
            AiProviderProfile.CurrentSchemaVersion,
            "OpenAI OAuth",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.OAuth(
                new SecretRef("missing-oauth-session"),
                AiProviderOAuthFlow.Device),
            "gpt-5.6-terra",
            order: 0);
        var handler = new StubHttpMessageHandler(
            (_, _) => throw new InvalidOperationException("HTTP must not run."));
        using var runtime = CreateRuntime(vault, handler);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.CredentialUnavailable, result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GitHubCopilotDiscoveryFiltersToSupportedModelFamilies()
    {
        using var vault = new InMemorySecretVault();
        var profileId = new AiProviderProfileId("provider-github-copilot");
        var reference = new SecretRef("provider-github-copilot-session");
        await new AiProviderOAuthVault(vault).StoreAsync(
            profileId,
            reference,
            new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "github-copilot",
                "copilot-access-token",
                refreshToken: null,
                expiresAt: DateTimeOffset.MaxValue),
            CancellationToken.None);
        var profile = new AiProviderProfile(
            profileId,
            AiProviderProfile.CurrentSchemaVersion,
            "GitHub Copilot",
            AiProviderKind.GitHubCopilot,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.GitHubCopilot),
            new AiProviderAuthentication.OAuth(reference, AiProviderOAuthFlow.Device),
            "gpt-5.6-terra",
            order: 0);
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """
            {"data":[
              {"id":"gpt-5.6-terra"},
              {"id":"gpt-5.3-codex"},
              {"id":"claude-sonnet-4.6"},
              {"id":"unsupported-preview"}
            ]}
            """));
        using var factory = new AiProviderFactory(vault, handler);

        var models = await factory.ListModelsAsync(profile, CancellationToken.None);

        Assert.Equal(
            ["claude-sonnet-4.6", "gpt-5.3-codex", "gpt-5.6-terra"],
            models.Select(model => model.Id), StringComparer.Ordinal);
        Assert.Equal(
            new Uri("https://api.githubcopilot.com/models"),
            handler.LastRequest!.Uri);
        Assert.Equal("Bearer copilot-access-token", handler.LastRequest.Authorization);
        Assert.Equal("conversation-edits", handler.LastRequest.OpenAiIntent);
    }

    [Fact]
    public async Task Credential_from_another_provider_scope_is_not_reused()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        var profile = ApiKeyProfile(
            AiProviderKind.OpenAi,
            "provider-requesting",
            reference);
        await StoreCredentialAsync(
            vault,
            new AiProviderProfileId("provider-owning"),
            reference,
            "wrong-provider-key");
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"data":[{"id":"unused"}]}"""));
        using var factory = new AiProviderFactory(vault, handler);

        var exception = await Assert.ThrowsAsync<AiProviderClientException>(async () =>
            await factory.ListModelsAsync(profile, CancellationToken.None));

        Assert.Equal(AiProviderRuntimeErrorCode.CredentialUnavailable, exception.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(
        HttpStatusCode.Unauthorized,
        AiProviderRuntimeErrorCode.AuthenticationFailed,
        "ai_provider_authentication_failed")]
    [InlineData(
        HttpStatusCode.Forbidden,
        AiProviderRuntimeErrorCode.AccessDenied,
        "ai_provider_access_denied")]
    public async Task Catalog_runtime_maps_provider_access_failures(
        HttpStatusCode statusCode,
        AiProviderRuntimeErrorCode expectedError,
        string expectedCode)
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    """{"sensitive":"must-not-escape"}""",
                    Encoding.UTF8,
                    "application/json"),
            }));
        using var runtime = CreateRuntime(vault, handler);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Equal(expectedCode, result.Code);
        Assert.DoesNotContain("must-not-escape", result.Message, StringComparison.Ordinal);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Catalog_runtime_preserves_bounded_retry_after_for_rate_limits()
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
        });
        using var runtime = CreateRuntime(vault, handler);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.RateLimited, result.ErrorCode);
        Assert.Equal("ai_provider_rate_limited", result.Code);
        Assert.Equal(TimeSpan.FromSeconds(17), result.RetryAfter);
    }

    [Fact]
    public async Task Redirect_response_is_rejected_instead_of_followed()
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://attacker.invalid/models");
            return Task.FromResult(response);
        });
        using var runtime = CreateRuntime(vault, handler);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.ProtocolError, result.ErrorCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Wrong_model_content_type_is_a_protocol_error()
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"model"}]}""",
                    Encoding.UTF8,
                    "text/html"),
            }));
        using var runtime = CreateRuntime(vault, handler);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.ProtocolError, result.ErrorCode);
    }

    [Theory]
    [InlineData("""{"data":[{"id":"same"},{"id":"same"}]}""")]
    [InlineData("""{"data":[],"data":[]}""")]
    [InlineData("""{"data":[}""")]
    [InlineData("""{"data":[42]}""")]
    [InlineData("""{"data":[{}]}""")]
    public async Task Duplicate_or_malformed_model_json_is_rejected(string payload)
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(payload));
        using var runtime = CreateRuntime(vault, handler);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.ProtocolError, result.ErrorCode);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Oversized_model_json_is_rejected_before_projection()
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var padding = new string('x', 1_100);
        var payload = $$"""{"data":[{"id":"model","padding":"{{padding}}"}]}""";
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(payload));
        var limits = new AiProviderRuntimeLimits(maximumModelResponseBytes: 1_024);
        using var runtime = CreateRuntime(vault, handler, limits);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.ResponseTooLarge, result.ErrorCode);
    }

    [Fact]
    public async Task Model_count_above_configured_bound_is_rejected()
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"data":[{"id":"one"},{"id":"two"}]}"""));
        var limits = new AiProviderRuntimeLimits(maximumModels: 1);
        using var runtime = CreateRuntime(vault, handler, limits);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.ResponseTooLarge, result.ErrorCode);
    }

    [Fact]
    public async Task Model_identifier_above_domain_bound_is_rejected()
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var oversizedId = new string('m', AiProviderProfile.MaximumModelIdLength + 1);
        var payload = $$"""{"data":[{"id":"{{oversizedId}}"}]}""";
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(payload));
        using var runtime = CreateRuntime(vault, handler);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.ProtocolError, result.ErrorCode);
    }

    [Fact]
    public async Task Empty_model_list_is_reported_as_model_unavailable()
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"data":[]}"""));
        using var runtime = CreateRuntime(vault, handler);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.ModelUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task Default_model_missing_keeps_discovered_models_for_correction()
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile(defaultModel: "configured-model");
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"data":[{"id":"available-model"}]}"""));
        using var runtime = CreateRuntime(vault, handler);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.ModelUnavailable, result.ErrorCode);
        Assert.Equal("ai_provider_model_unavailable", result.Code);
        Assert.Equal("available-model", Assert.Single(result.Models).Id);
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_without_sending_http()
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"data":[{"id":"unused"}]}"""));
        using var runtime = CreateRuntime(vault, handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await runtime.TestAsync(profile, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.Cancelled, result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Discovery_deadline_is_reported_as_timeout()
    {
        using var vault = new InMemorySecretVault();
        var profile = LoopbackProfile();
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled delay unexpectedly completed.");
        });
        var limits = new AiProviderRuntimeLimits(
            discoveryTimeout: TimeSpan.FromMilliseconds(100));
        using var runtime = CreateRuntime(vault, handler, limits);

        var result = await runtime.TestAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AiProviderRuntimeErrorCode.Timeout, result.ErrorCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Disposing_from_streaming_notification_cancels_without_a_token_lifetime_race()
    {
        var profile = LoopbackProfile();
        var catalog = new FixedDefinitionCatalog(Snapshot(profile));
        using var vault = new InMemorySecretVault();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            }));
        using var factory = new AiProviderFactory(vault, handler);
        using var runtime = new CatalogAiProviderRuntime(catalog, factory);
        runtime.Changed += (_, _) =>
        {
            if (runtime.Snapshot.IsStreaming)
            {
                runtime.Dispose();
            }
        };

        var result = await runtime.SendAsync(
            profile.Id,
            "Cancel during startup.",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("agent_chat_cancelled", result.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Catalog_runtime_projects_ordered_profiles_and_refreshes_on_change()
    {
        var slow = LoopbackProfile(
            id: "provider-slow",
            name: "Slow",
            order: 20,
            isEnabled: false);
        var fast = ApiKeyProfile(
            AiProviderKind.OpenAi,
            "provider-fast",
            SecretRef.New(),
            name: "Fast",
            order: 1);
        var catalog = new FixedDefinitionCatalog(Snapshot(slow, fast));
        using var vault = new InMemorySecretVault();
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"data":[{"id":"unused"}]}"""));
        using var factory = new AiProviderFactory(vault, handler);
        using var runtime = new CatalogAiProviderRuntime(catalog, factory);
        var changed = 0;
        runtime.ProfilesChanged += (_, _) => changed++;

        Assert.Equal(
            [fast.Id, slow.Id],
            runtime.Profiles.Select(profile => profile.Id));
        Assert.True(runtime.Profiles[0].RequiresCredential);
        Assert.False(runtime.Profiles[1].RequiresCredential);
        Assert.True(runtime.Profiles[0].SupportsReasoning);
        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Off,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
            ],
            runtime.Profiles[0].SupportedReasoningEfforts.ToArray());
        Assert.False(runtime.Profiles[1].SupportsReasoning);
        Assert.Equal(
            [AgentReasoningEffort.Automatic],
            runtime.Profiles[1].SupportedReasoningEfforts.ToArray());
        var diagnostic = Assert.Single(runtime.Diagnostics);
        Assert.Equal(slow.Id, diagnostic.ProfileId);
        Assert.Equal("ai_provider_disabled", diagnostic.Code);

        catalog.Publish(Snapshot(fast));

        Assert.Equal(1, changed);
        Assert.Equal(fast.Id, Assert.Single(runtime.Profiles).Id);
        Assert.Empty(runtime.Diagnostics);
    }

    [Fact]
    public async Task Catalog_runtime_discovers_and_projects_provider_models()
    {
        var reference = SecretRef.New();
        var profile = ApiKeyProfile(
            AiProviderKind.OpenAi,
            "provider-discovery",
            reference,
            defaultModel: "gpt-default");
        var catalog = new FixedDefinitionCatalog(Snapshot(profile));
        using var vault = new InMemorySecretVault();
        await StoreCredentialAsync(vault, profile.Id, reference, "provider-key");
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"data":[{"id":"gpt-default","display_name":"Default"},{"id":"gpt-fast","display_name":"Fast"}]}"""));
        using var factory = new AiProviderFactory(vault, handler);
        using var runtime = new CatalogAiProviderRuntime(catalog, factory);

        var result = await runtime.DiscoverModelsAsync(
            profile.Id,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["gpt-default", "gpt-fast"], result.Models.Select(model => model.Id), StringComparer.Ordinal);
        Assert.Equal(
            ["gpt-default", "gpt-fast"],
            Assert.Single(runtime.Profiles).Models.Select(model => model.Id), StringComparer.Ordinal);
        Assert.Equal(1, handler.CallCount);
        Assert.EndsWith("/v1/models", handler.LastRequest!.Uri.AbsoluteUri);
        var changed = new AiProviderProfile(profile.Id, profile.SchemaVersion, profile.Name,
            profile.ProviderKind, new Uri("https://replacement.example/v1/"), profile.Authentication,
            "replacement-model", profile.Order);
        catalog.Publish(Snapshot(changed));
        Assert.Equal(["replacement-model"], Assert.Single(runtime.Profiles).Models.Select(model => model.Id), StringComparer.Ordinal);
    }

    [Fact]
    public void Saved_custom_catalog_populates_all_policy_pickers_without_network_discovery()
    {
        var profile = new AiProviderProfile(AiProviderProfileId.New(), AiProviderProfile.CurrentSchemaVersion,
            "Custom", AiProviderKind.OpenAiCompatible, new Uri("http://localhost:11434/v1/"),
            new AiProviderAuthentication.None(), "custom/manual", 0,
            discoveredModelIds: ["custom/primary", "custom/fast"]);
        var catalog = new FixedDefinitionCatalog(Snapshot(profile));
        using var vault = new InMemorySecretVault();
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("No discovery expected."));
        using var factory = new AiProviderFactory(vault, handler);
        using var runtime = new CatalogAiProviderRuntime(catalog, factory);
        Assert.Equal(["custom/manual", "custom/primary", "custom/fast"],
            Assert.Single(runtime.Profiles).Models.Select(model => model.Id), StringComparer.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Catalog_runtime_disables_profiles_whose_protocol_is_not_implemented()
    {
        var profile = ApiKeyProfile(
            AiProviderKind.Google,
            "provider-google-unavailable",
            SecretRef.New());
        var catalog = new FixedDefinitionCatalog(Snapshot(profile));
        using var vault = new InMemorySecretVault();
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"models":[]}"""));
        using var factory = new AiProviderFactory(vault, handler);
        using var runtime = new CatalogAiProviderRuntime(catalog, factory);

        var descriptor = Assert.Single(runtime.Profiles);
        Assert.False(descriptor.IsEnabled);
        Assert.False(descriptor.SupportsImageInput);
        Assert.False(descriptor.SupportsReasoning);
        var diagnostic = Assert.Single(runtime.Diagnostics);
        Assert.Equal(profile.Id, diagnostic.ProfileId);
        Assert.Equal(AiProviderRuntimeDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("ai_provider_runtime_unavailable", diagnostic.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Catalog_runtime_projects_model_routed_reasoning_effort_sets()
    {
        var adaptive = ApiKeyProfile(
            AiProviderKind.Anthropic,
            "provider-anthropic-adaptive",
            SecretRef.New(),
            defaultModel: "claude-opus-4-6");
        var nonAdaptive = ApiKeyProfile(
            AiProviderKind.Anthropic,
            "provider-anthropic-standard",
            SecretRef.New(),
            defaultModel: "claude-sonnet-4-5");
        var copilotChat = new AiProviderProfile(
            new AiProviderProfileId("provider-copilot-chat"),
            AiProviderProfile.CurrentSchemaVersion,
            "Copilot chat",
            AiProviderKind.GitHubCopilot,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.GitHubCopilot),
            new AiProviderAuthentication.OAuth(
                new SecretRef("copilot-chat-session"),
                AiProviderOAuthFlow.Device),
            "gpt-5.6-terra",
            order: 2);
        var copilotResponses = new AiProviderProfile(
            new AiProviderProfileId("provider-copilot-responses"),
            AiProviderProfile.CurrentSchemaVersion,
            "Copilot Responses",
            AiProviderKind.GitHubCopilot,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.GitHubCopilot),
            new AiProviderAuthentication.OAuth(
                new SecretRef("copilot-responses-session"),
                AiProviderOAuthFlow.Device),
            "gpt-5.3-codex",
            order: 3);
        var catalog = new FixedDefinitionCatalog(
            Snapshot(adaptive, nonAdaptive, copilotChat, copilotResponses));
        using var vault = new InMemorySecretVault();
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync("{}"));
        using var factory = new AiProviderFactory(vault, handler);
        using var runtime = new CatalogAiProviderRuntime(catalog, factory);

        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Off,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
                AgentReasoningEffort.ExtraHigh,
            ],
            runtime.Profiles.Single(profile => profile.Id == adaptive.Id)
                .SupportedReasoningEfforts.ToArray());
        Assert.Equal(
            [AgentReasoningEffort.Automatic],
            runtime.Profiles.Single(profile => profile.Id == nonAdaptive.Id)
                .SupportedReasoningEfforts.ToArray());
        Assert.Equal(
            [AgentReasoningEffort.Automatic],
            runtime.Profiles.Single(profile => profile.Id == copilotChat.Id)
                .SupportedReasoningEfforts.ToArray());
        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Off,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
                AgentReasoningEffort.ExtraHigh,
            ],
            runtime.Profiles.Single(profile => profile.Id == copilotResponses.Id)
                .SupportedReasoningEfforts.ToArray());
    }

    [Fact]
    public void Catalog_runtime_projects_codex_context_windows_for_compaction()
    {
        var profile = new AiProviderProfile(
            new AiProviderProfileId("provider-openai-context"),
            AiProviderProfile.CurrentSchemaVersion,
            "OpenAI",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.OAuth(
                new SecretRef("openai-context-session"),
                AiProviderOAuthFlow.Browser),
            "gpt-5.6-terra",
            order: 0);
        using var vault = new InMemorySecretVault();
        using var factory = new AiProviderFactory(
            vault,
            new StubHttpMessageHandler((_, _) => JsonResponseAsync("{}")));
        using var runtime = new CatalogAiProviderRuntime(
            new FixedDefinitionCatalog(Snapshot(profile)),
            factory);

        var descriptor = Assert.Single(runtime.Profiles);

        Assert.Equal(
            272_000,
            descriptor.Models.Single(model => string.Equals(model.Id, "gpt-5.6-terra", StringComparison.Ordinal))
                .ContextWindowTokens);
        Assert.Equal(
            128_000,
            descriptor.Models.Single(model => string.Equals(model.Id, "gpt-5.3-codex-spark", StringComparison.Ordinal))
                .ContextWindowTokens);
    }

    [Fact]
    public void Pinned_provider_binding_detects_profile_revision_changes()
    {
        var profile = LoopbackProfile();
        var catalog = new FixedDefinitionCatalog(SnapshotAt(4, profile));
        using var vault = new InMemorySecretVault();
        var handler = new StubHttpMessageHandler((_, _) => JsonResponseAsync(
            """{"data":[{"id":"unused"}]}"""));
        using var factory = new AiProviderFactory(vault, handler);
        using var runtime = new CatalogAiProviderRuntime(catalog, factory);

        var binding = runtime.PinProvider(profile.Id);
        var firstAdapter = binding.CreateProvider();
        var secondAdapter = binding.CreateProvider();

        Assert.Equal(profile.Id, binding.ProfileId);
        Assert.Equal(4, binding.Revision);
        Assert.Equal(profile.DefaultModel, binding.DefaultModel);
        Assert.True(binding.IsCurrent);
        Assert.NotSame(firstAdapter, secondAdapter);

        catalog.Publish(SnapshotAt(
            5,
            LoopbackProfile(
                id: profile.Id.Value,
                name: "Edited profile")));

        Assert.False(binding.IsCurrent);
        var replacement = runtime.PinProvider(profile.Id);
        Assert.Equal(5, replacement.Revision);
        Assert.True(replacement.IsCurrent);
    }

    [Fact]
    public void Pinned_provider_binding_accepts_only_a_loopback_socks_route()
    {
        var profile = ApiKeyProfile(
            AiProviderKind.OpenAi,
            "provider-routed",
            new SecretRef("provider-routed-key"));
        using var vault = new InMemorySecretVault();
        using var runtime = new CatalogAiProviderRuntime(
            new FixedDefinitionCatalog(Snapshot(profile)),
            vault);
        var binding = runtime.PinProvider(profile.Id);

        var provider = binding.CreateProvider(
            profile.DefaultModel,
            new Uri("socks5://127.0.0.1:43210"));

        Assert.NotNull(provider);
        Assert.Throws<ArgumentException>(() => binding.CreateProvider(
            profile.DefaultModel,
            new Uri("http://127.0.0.1:43210")));
        Assert.Throws<ArgumentException>(() => binding.CreateProvider(
            profile.DefaultModel,
            new Uri("socks5://example.com:43210")));
    }

    [Fact]
    public async Task Pinned_provider_binding_forwards_the_exact_requested_model()
    {
        const string selectedModel = "policy-selected-model";
        string? requestBody = null;
        var profile = LoopbackProfile(defaultModel: "profile-default-model");
        var catalog = new FixedDefinitionCatalog(Snapshot(profile));
        using var vault = new InMemorySecretVault();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":null}]}

                    data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        });
        using var factory = new AiProviderFactory(vault, handler);
        using var runtime = new CatalogAiProviderRuntime(catalog, factory);
        var binding = runtime.PinProvider(profile.Id);
        var provider = binding.CreateProvider(selectedModel);
        var session = new NativeAgentSession(
            new AgentRunId("provider-binding-model-test"));

        await session.RunTurnAsync(
            "Use the pinned model.",
            [],
            provider,
            CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        using var body = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        Assert.Equal(
            selectedModel,
            body.RootElement.GetProperty("model").GetString());
    }

    private static CatalogAiProviderRuntime CreateRuntime(
        ISecretVault vault,
        HttpMessageHandler handler,
        AiProviderRuntimeLimits? limits = null)
    {
        var factory = new AiProviderFactory(vault, handler, limits);
        return new CatalogAiProviderRuntime(
            new FixedDefinitionCatalog(DefinitionCatalogSnapshot.Empty),
            factory);
    }

    private static AiProviderProfile ApiKeyProfile(
        AiProviderKind providerKind,
        string id,
        SecretRef reference,
        string? name = null,
        int order = 0,
        bool isEnabled = true,
        string? defaultModel = null) =>
        new(
            new AiProviderProfileId(id),
            AiProviderProfile.CurrentSchemaVersion,
            name ?? id,
            providerKind,
            AiProviderProfile.DefaultEndpoint(providerKind),
            new AiProviderAuthentication.ApiKey(reference),
            defaultModel
                ?? (providerKind == AiProviderKind.Anthropic
                    ? "claude-test"
                    : "gpt-test"),
            order,
            isEnabled);

    private static AiProviderProfile LoopbackProfile(
        string id = "provider-local",
        string name = "Local",
        string defaultModel = "local-model",
        int order = 0,
        bool isEnabled = true) =>
        new(
            new AiProviderProfileId(id),
            AiProviderProfile.CurrentSchemaVersion,
            name,
            AiProviderKind.OpenAiCompatible,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAiCompatible),
            new AiProviderAuthentication.None(),
            defaultModel,
            order,
            isEnabled);

    private static async Task StoreCredentialAsync(
        ISecretVault vault,
        AiProviderProfileId profileId,
        SecretRef reference,
        string credential)
    {
        var scope = new SecretScope(SecretScopeKind.AiProvider, profileId.Value);
        var purpose = new SecretUsePurpose(
            SecretUseKind.AiProviderAuthentication,
            profileId.Value);
        using var material = SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(credential));
        var result = await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "Provider API key",
                SecretKind.ApiKey,
                scope,
                purpose),
            material,
            CancellationToken.None);
        Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(result);
    }

    private static DefinitionCatalogSnapshot Snapshot(
        params AiProviderProfile[] profiles) =>
        SnapshotAt(1, profiles);

    private static DefinitionCatalogSnapshot SnapshotAt(
        long revision,
        params AiProviderProfile[] profiles) =>
        DefinitionCatalogSnapshot.Empty with
        {
            AiProviderProfiles = [.. profiles
                .Select(profile => new StoredDefinition<AiProviderProfile>(
                    profile,
                    revision,
                    StoredAt,
                    StoredAt))],
        };

    private static Task<HttpResponseMessage> JsonResponseAsync(string payload) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public RequestSnapshot? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = RequestSnapshot.From(request);
            var response = await respond(request, cancellationToken).ConfigureAwait(false);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        string? Accept,
        string? Authorization,
        string? ApiKey,
        string? AnthropicVersion,
        string? GoogleApiKey,
        string? OpenAiIntent,
        string? ChatGptAccountId)
    {
        public static RequestSnapshot From(HttpRequestMessage request) => new(
            request.Method,
            request.RequestUri!,
            request.Headers.Accept.SingleOrDefault()?.MediaType,
            request.Headers.Authorization?.ToString(),
            Header(request, "x-api-key"),
            Header(request, "anthropic-version"),
            Header(request, "x-goog-api-key"),
            Header(request, "Openai-Intent"),
            Header(request, "ChatGPT-Account-Id"));

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values)
                ? values.SingleOrDefault()
                : null;
    }

    private sealed class RecordingSecretVault : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();

        public SecretVaultAvailability Availability => _inner.Availability;

        public ResolveSecretRequest? LastResolveRequest { get; private set; }

        public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
            CreateSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) =>
            _inner.CreateAsync(request, material, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken cancellationToken)
        {
            LastResolveRequest = request;
            return _inner.ResolveAsync(request, cancellationToken);
        }

        public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
            ReplaceSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) =>
            _inner.ReplaceAsync(request, material, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
            RelabelSecretRequest request,
            CancellationToken cancellationToken) =>
            _inner.RelabelAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
            DeleteSecretRequest request,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
            GetSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            _inner.GetMetadataAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
            ListSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            _inner.ListMetadataAsync(request, cancellationToken);

        public void Dispose() => _inner.Dispose();
    }

    private sealed class FixedDefinitionCatalog(
        DefinitionCatalogSnapshot snapshot)
        : IDefinitionCatalog
    {
        public DefinitionCatalogSnapshot Snapshot { get; private set; } = snapshot;

        public event EventHandler? Changed;

        public void Publish(DefinitionCatalogSnapshot next)
        {
            Snapshot = next;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
            ConnectionProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutAsync(
            LayoutDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveScreenAsync(
            ScreenDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
            WorkspaceDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
            ThemePreference definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
            TerminalProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
            KeymapProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> SaveFileProviderProfileAsync(
            FileProviderProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> SaveAiProviderProfileAsync(
            AiProviderProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>> SaveMcpServerProfileAsync(
            McpServerProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
            QuickTerminalSettings definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
            DefinitionKey key,
            long expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
