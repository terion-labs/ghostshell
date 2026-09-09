using System.Collections.ObjectModel;

namespace Asura.Core;

/// <summary>
/// Provider identity registry. This is configuration metadata only: credentials
/// remain opaque vault references and protocol implementations live in the
/// provider runtime project.
/// </summary>
public static class AiProviderCatalog
{
    private static readonly IReadOnlyList<AiProviderDefinition> RegisteredDefinitions =
        BuildDefinitions();
    private static readonly IReadOnlyDictionary<AiProviderKind, AiProviderDefinition>
        DefinitionsByIdentity = new ReadOnlyDictionary<AiProviderKind, AiProviderDefinition>(
            RegisteredDefinitions.ToDictionary(definition => definition.Identity));

    public static IReadOnlyList<AiProviderDefinition> Definitions => RegisteredDefinitions;

    public static AiProviderDefinition Get(AiProviderKind identity)
    {
        if (DefinitionsByIdentity.TryGetValue(identity, out var definition))
        {
            return definition;
        }

        throw new ArgumentOutOfRangeException(nameof(identity), identity, null);
    }

    private static IReadOnlyList<AiProviderDefinition> BuildDefinitions()
    {
        var definitions = new[]
        {
            Define(
                AiProviderKind.Anthropic,
                "Anthropic",
                AiProviderCategory.Direct,
                AiProviderProtocol.AnthropicMessages,
                "https://api.anthropic.com/v1/",
                AiProviderAuthenticationMethod.ApiKey,
                AiProviderCredentialPlacement.AnthropicApiKeyHeader,
                AiProviderModelDiscoveryKind.AnthropicModels,
                new(true, true, true, true, true)),
            Define(
                AiProviderKind.OpenAi,
                "OpenAI",
                AiProviderCategory.Direct,
                AiProviderProtocol.OpenAiResponses,
                "https://api.openai.com/v1/",
                AiProviderAuthenticationMethod.ApiKey
                | AiProviderAuthenticationMethod.OAuthBrowser
                | AiProviderAuthenticationMethod.OAuthDevice,
                AiProviderCredentialPlacement.AuthorizationBearer,
                AiProviderModelDiscoveryKind.OpenAiModels,
                AiProviderCapabilities.Responses),
            Define(
                AiProviderKind.Google,
                "Google",
                AiProviderCategory.Direct,
                AiProviderProtocol.GoogleGenerativeAi,
                "https://generativelanguage.googleapis.com/v1beta/",
                AiProviderAuthenticationMethod.ApiKey,
                AiProviderCredentialPlacement.GoogleApiKeyHeader,
                AiProviderModelDiscoveryKind.GoogleModels,
                new(true, true, true, false, true),
                isRuntimeSupported: false),
            Define(
                AiProviderKind.XAi,
                "xAI",
                AiProviderCategory.Direct,
                AiProviderProtocol.OpenAiChatCompletions,
                "https://api.x.ai/v1/",
                AiProviderAuthenticationMethod.ApiKey,
                AiProviderCredentialPlacement.AuthorizationBearer,
                AiProviderModelDiscoveryKind.OpenAiModels,
                new(true, true, true, false, true)),
            Define(
                AiProviderKind.DeepSeek,
                "DeepSeek",
                AiProviderCategory.Direct,
                AiProviderProtocol.OpenAiChatCompletions,
                "https://api.deepseek.com/v1/",
                AiProviderAuthenticationMethod.ApiKey,
                AiProviderCredentialPlacement.AuthorizationBearer,
                AiProviderModelDiscoveryKind.OpenAiModels,
                new(true, true, false, false, true)),
            Define(
                AiProviderKind.MoonshotAi,
                "Moonshot AI",
                AiProviderCategory.Direct,
                AiProviderProtocol.OpenAiChatCompletions,
                "https://api.moonshot.ai/v1/",
                AiProviderAuthenticationMethod.ApiKey,
                AiProviderCredentialPlacement.AuthorizationBearer,
                AiProviderModelDiscoveryKind.OpenAiModels,
                new(true, true, true, false, true)),
            Define(
                AiProviderKind.OpenRouter,
                "OpenRouter",
                AiProviderCategory.Gateway,
                AiProviderProtocol.OpenAiChatCompletions,
                "https://openrouter.ai/api/v1/",
                AiProviderAuthenticationMethod.ApiKey,
                AiProviderCredentialPlacement.AuthorizationBearer,
                AiProviderModelDiscoveryKind.OpenAiModels,
                AiProviderCapabilities.ChatCompletions),
            Define(
                AiProviderKind.GitHubCopilot,
                "GitHub Copilot",
                AiProviderCategory.Gateway,
                // Copilot selects its wire shape per model: Codex models use
                // Responses while the remaining supported families use chat completions.
                AiProviderProtocol.GitHubCopilot,
                "https://api.githubcopilot.com/",
                AiProviderAuthenticationMethod.OAuthDevice,
                AiProviderCredentialPlacement.AuthorizationBearer,
                AiProviderModelDiscoveryKind.OpenAiModels,
                AiProviderCapabilities.Responses),
            Define(
                AiProviderKind.Bedrock,
                "Amazon Bedrock",
                AiProviderCategory.Gateway,
                AiProviderProtocol.AmazonBedrockConverse,
                "https://bedrock-runtime.us-east-1.amazonaws.com/",
                AiProviderAuthenticationMethod.AwsCredentialChain,
                AiProviderCredentialPlacement.AwsSignatureVersion4,
                AiProviderModelDiscoveryKind.None,
                new(true, true, true, false, false),
                isRuntimeSupported: false),
            Define(
                AiProviderKind.Ollama,
                "Ollama",
                AiProviderCategory.Local,
                AiProviderProtocol.OpenAiChatCompletions,
                "http://localhost:11434/v1/",
                AiProviderAuthenticationMethod.NoAuthentication
                | AiProviderAuthenticationMethod.ApiKey,
                AiProviderCredentialPlacement.AuthorizationBearer,
                AiProviderModelDiscoveryKind.OpenAiModels,
                AiProviderCapabilities.ChatCompletions),
            Define(
                AiProviderKind.OpenAiCompatible,
                "OpenAI-compatible",
                AiProviderCategory.Custom,
                AiProviderProtocol.OpenAiChatCompletions,
                "http://localhost:11434/v1/",
                AiProviderAuthenticationMethod.NoAuthentication
                | AiProviderAuthenticationMethod.ApiKey,
                AiProviderCredentialPlacement.AuthorizationBearer,
                AiProviderModelDiscoveryKind.OpenAiModels,
                AiProviderCapabilities.ChatCompletions),
        };

        return Array.AsReadOnly(definitions);
    }

    private static AiProviderDefinition Define(
        AiProviderKind identity,
        string displayName,
        AiProviderCategory category,
        AiProviderProtocol protocol,
        string endpoint,
        AiProviderAuthenticationMethod authenticationMethods,
        AiProviderCredentialPlacement credentialPlacement,
        AiProviderModelDiscoveryKind modelDiscovery,
        AiProviderCapabilities capabilities,
        bool isRuntimeSupported = true) =>
        new(
            identity,
            displayName,
            category,
            protocol,
            isRuntimeSupported,
            new Uri(endpoint, UriKind.Absolute),
            authenticationMethods,
            credentialPlacement,
            modelDiscovery,
            capabilities);
}
