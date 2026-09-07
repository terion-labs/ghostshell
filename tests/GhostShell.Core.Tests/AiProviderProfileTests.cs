using System.Text.Json;

namespace GhostShell.Core.Tests;

public sealed class AiProviderProfileTests
{
    [Fact]
    public void Discovered_models_are_immutable_and_survive_serialization()
    {
        string[] models = ["custom/primary", "custom/fast", "custom/fast"];
        var profile = new AiProviderProfile(AiProviderProfileId.New(), AiProviderProfile.CurrentSchemaVersion,
            "Custom", AiProviderKind.OpenAiCompatible, new Uri("http://localhost:11434/v1/"),
            new AiProviderAuthentication.None(), "custom/primary", 0, discoveredModelIds: models);
        models[0] = "changed";
        var restored = JsonSerializer.Deserialize<AiProviderProfile>(JsonSerializer.Serialize(profile));
        Assert.Equal(["custom/primary", "custom/fast"], Assert.IsType<AiProviderProfile>(restored).DiscoveredModelIds);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(profile))!.AsObject();
        legacy.Remove("DiscoveredModelIds");
        Assert.Empty(JsonSerializer.Deserialize<AiProviderProfile>(legacy.ToJsonString())!.DiscoveredModelIds);
    }

    [Fact]
    public void ApiKeyIsPersistedOnlyAsAnOpaqueSecretReference()
    {
        var secret = new SecretRef("vault-ai-openai");
        var profile = CreateProfile(
            AiProviderKind.OpenAi,
            new Uri("https://api.openai.com/v1"),
            new AiProviderAuthentication.ApiKey(secret));

        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<AiProviderProfile>(json);

        Assert.NotNull(restored);
        Assert.Equal(secret, Assert.IsType<AiProviderAuthentication.ApiKey>(
            restored.Authentication).Secret);
        Assert.Equal(new Uri("https://api.openai.com/v1/"), restored.Endpoint);
        Assert.DoesNotContain("secretValue", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NonCurrentSchemaIsRejected()
    {
        const string json =
            """
            {
              "Id": { "Value": "non-current-openai" },
              "SchemaVersion": 1,
              "Name": "Non-current OpenAI",
              "ProviderKind": 1,
              "Endpoint": "https://api.openai.com/v1/",
              "Authentication": {
                "$type": "api-key",
                "Secret": { "Value": "non-current-openai-key" }
              },
              "DefaultModel": "gpt-test",
              "Order": 0,
              "IsEnabled": true
            }
            """;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            JsonSerializer.Deserialize<AiProviderProfile>(json));

        Assert.Equal("schemaVersion", exception.ParamName);
    }

    [Fact]
    public void CatalogDefinesEveryProviderIdentityExactlyOnce()
    {
        var definitions = AiProviderCatalog.Definitions;

        Assert.Equal(Enum.GetValues<AiProviderKind>().Length, definitions.Count);
        Assert.Equal(
            definitions.Count,
            definitions.Select(definition => definition.Identity).Distinct().Count());
        Assert.All(
            definitions,
            definition => Assert.Equal(
                definition.DefaultEndpoint,
                AiProviderProfile.DefaultEndpoint(definition.Identity)));
        Assert.Equal(
            AiProviderProtocol.OpenAiResponses,
            AiProviderCatalog.Get(AiProviderKind.OpenAi).Protocol);
        Assert.Equal(
            AiProviderProtocol.OpenAiChatCompletions,
            AiProviderCatalog.Get(AiProviderKind.OpenRouter).Protocol);
        Assert.Equal(
            AiProviderCategory.Local,
            AiProviderCatalog.Get(AiProviderKind.Ollama).Category);
        Assert.Equal(
            AiProviderCategory.Custom,
            AiProviderCatalog.Get(AiProviderKind.OpenAiCompatible).Category);
        Assert.False(AiProviderCatalog.Get(AiProviderKind.Google).IsRuntimeSupported);
        Assert.False(AiProviderCatalog.Get(AiProviderKind.Bedrock).IsRuntimeSupported);
        Assert.False(AiProviderCatalog.Get(AiProviderKind.XAi).Capabilities.SupportsReasoning);
        Assert.False(AiProviderCatalog.Get(AiProviderKind.DeepSeek).Capabilities.SupportsReasoning);
        Assert.False(AiProviderCatalog.Get(AiProviderKind.MoonshotAi).Capabilities.SupportsReasoning);
        Assert.False(AiProviderCatalog.Get(AiProviderKind.Google).Capabilities.SupportsReasoning);
        Assert.False(AiProviderCatalog.Get(AiProviderKind.Bedrock).Capabilities.SupportsReasoning);
    }

    [Fact]
    public void OAuthAndAwsAuthenticationPersistOnlyVaultOrPlatformReferences()
    {
        var openAi = CreateProfile(
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.OAuth(
                new SecretRef("openai-oauth-session"),
                AiProviderOAuthFlow.Device));
        var bedrock = CreateProfile(
            AiProviderKind.Bedrock,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.Bedrock),
            new AiProviderAuthentication.AwsCredentialChain());

        var openAiJson = JsonSerializer.Serialize(openAi);
        var restoredOpenAi = JsonSerializer.Deserialize<AiProviderProfile>(openAiJson);
        var restoredBedrock = JsonSerializer.Deserialize<AiProviderProfile>(
            JsonSerializer.Serialize(bedrock));

        var oauth = Assert.IsType<AiProviderAuthentication.OAuth>(
            Assert.IsType<AiProviderProfile>(restoredOpenAi).Authentication);
        Assert.Equal(new SecretRef("openai-oauth-session"), oauth.Session);
        Assert.Equal(AiProviderOAuthFlow.Device, oauth.Flow);
        Assert.IsType<AiProviderAuthentication.AwsCredentialChain>(
            Assert.IsType<AiProviderProfile>(restoredBedrock).Authentication);
        Assert.DoesNotContain("access_token", openAiJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", openAiJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderIdentityRejectsUnsupportedAuthenticationMethod()
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
            AiProviderKind.GitHubCopilot,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.GitHubCopilot),
            new AiProviderAuthentication.ApiKey(new SecretRef("copilot-key"))));
    }

    [Fact]
    public void ProfileCapabilitiesCanNarrowButCannotExceedIdentityCeiling()
    {
        var narrowed = new AiProviderProfile(
            new AiProviderProfileId("narrowed-provider"),
            AiProviderProfile.CurrentSchemaVersion,
            "Narrowed provider",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.ApiKey(new SecretRef("openai-key")),
            "gpt-test",
            order: 0,
            isEnabled: true,
            protocol: AiProviderProtocol.OpenAiResponses,
            capabilities: new AiProviderCapabilities(
                SupportsToolCalling: true,
                SupportsToolBatches: false,
                SupportsImageInput: false,
                SupportsReasoning: false,
                SupportsModelDiscovery: true));

        Assert.False(narrowed.Capabilities.SupportsImageInput);
        Assert.Throws<ArgumentException>(() => new AiProviderProfile(
            new AiProviderProfileId("elevated-provider"),
            AiProviderProfile.CurrentSchemaVersion,
            "Elevated provider",
            AiProviderKind.DeepSeek,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.DeepSeek),
            new AiProviderAuthentication.ApiKey(new SecretRef("deepseek-key")),
            "deepseek-model",
            order: 0,
            isEnabled: true,
            protocol: AiProviderProtocol.OpenAiChatCompletions,
            capabilities: AiProviderCatalog.Get(AiProviderKind.DeepSeek).Capabilities with
            {
                SupportsImageInput = true,
            }));
    }

    [Theory]
    [InlineData("http://api.openai.com/v1/")]
    [InlineData("https://key@example.test/v1/")]
    [InlineData("https://example.test/v1/?tenant=secret")]
    [InlineData("file:///tmp/provider")]
    public void UnsafeProviderEndpointsAreRejected(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
            AiProviderKind.OpenAiCompatible,
            new Uri(endpoint),
            new AiProviderAuthentication.ApiKey(new SecretRef("provider-key"))));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1/")]
    [InlineData("http://127.0.0.1:1234/v1/")]
    [InlineData("http://[::1]:11434/v1/")]
    public void LoopbackProvidersMayExplicitlyUseNoAuthentication(string endpoint)
    {
        var profile = CreateProfile(
            AiProviderKind.OpenAiCompatible,
            new Uri(endpoint),
            new AiProviderAuthentication.None());

        Assert.IsType<AiProviderAuthentication.None>(profile.Authentication);
    }

    [Fact]
    public void RemoteProvidersCannotDisableAuthentication()
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
            AiProviderKind.OpenAiCompatible,
            new Uri("https://gateway.example.test/v1/"),
            new AiProviderAuthentication.None()));
    }

    [Fact]
    public void IdentityModelAndOrderingAreBounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiProviderProfile(
            new AiProviderProfileId("provider"),
            schemaVersion: AiProviderProfile.CurrentSchemaVersion + 1,
            "Provider",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.ApiKey(new SecretRef("provider-key")),
            "gpt",
            order: 0));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.ApiKey(new SecretRef("provider-key")),
            defaultModel: "gpt\ninjected"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiProviderProfile(
            new AiProviderProfileId("provider"),
            AiProviderProfile.CurrentSchemaVersion,
            "Provider",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.ApiKey(new SecretRef("provider-key")),
            "gpt",
            order: AiProviderProfile.MaximumOrder + 1));
    }

    private static AiProviderProfile CreateProfile(
        AiProviderKind providerKind,
        Uri endpoint,
        AiProviderAuthentication authentication,
        string defaultModel = "model") =>
        new(
            new AiProviderProfileId("provider"),
            AiProviderProfile.CurrentSchemaVersion,
            "Provider",
            providerKind,
            endpoint,
            authentication,
            defaultModel,
            order: 0);
}
