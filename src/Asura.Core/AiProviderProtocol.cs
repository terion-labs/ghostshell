namespace Asura.Core;

/// <summary>
/// Identifies the provider wire contract independently of the company or service
/// serving it. Several provider identities can therefore share one adapter without
/// losing their own endpoints, authentication rules, or discovery behavior.
/// </summary>
public enum AiProviderProtocol
{
    AnthropicMessages,
    OpenAiResponses,
    OpenAiChatCompletions,
    GitHubCopilot,
    GoogleGenerativeAi,
    AmazonBedrockConverse,
}

public enum AiProviderModelDiscoveryKind
{
    None,
    AnthropicModels,
    OpenAiModels,
    GoogleModels,
    AmazonBedrockModels,
}

public enum AiProviderCredentialPlacement
{
    None,
    AuthorizationBearer,
    AnthropicApiKeyHeader,
    GoogleApiKeyHeader,
    AwsSignatureVersion4,
}
