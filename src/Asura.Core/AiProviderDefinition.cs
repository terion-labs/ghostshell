namespace Asura.Core;

[Flags]
public enum AiProviderAuthenticationMethod
{
    None = 0,
    NoAuthentication = 1 << 0,
    ApiKey = 1 << 1,
    OAuthBrowser = 1 << 2,
    OAuthDevice = 1 << 3,
    AwsCredentialChain = 1 << 4,
}

public enum AiProviderCategory
{
    Direct,
    Gateway,
    Local,
    Custom,
}

/// <summary>
/// Non-secret metadata for one first-class provider identity.
/// </summary>
public sealed record AiProviderDefinition(
    AiProviderKind Identity,
    string DisplayName,
    AiProviderCategory Category,
    AiProviderProtocol Protocol,
    bool IsRuntimeSupported,
    Uri DefaultEndpoint,
    AiProviderAuthenticationMethod AuthenticationMethods,
    AiProviderCredentialPlacement CredentialPlacement,
    AiProviderModelDiscoveryKind ModelDiscovery,
    AiProviderCapabilities Capabilities);
