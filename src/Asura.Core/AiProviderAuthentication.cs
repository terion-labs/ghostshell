using System.Text.Json.Serialization;

namespace Asura.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AiProviderAuthentication.None), "none")]
[JsonDerivedType(typeof(AiProviderAuthentication.ApiKey), "api-key")]
[JsonDerivedType(typeof(AiProviderAuthentication.OAuth), "oauth")]
[JsonDerivedType(typeof(AiProviderAuthentication.AwsCredentialChain), "aws-credential-chain")]
public abstract record AiProviderAuthentication
{
    private AiProviderAuthentication()
    {
    }

    public sealed record None : AiProviderAuthentication;

    public sealed record ApiKey : AiProviderAuthentication
    {
        [JsonConstructor]
        public ApiKey(SecretRef secret)
        {
            RuntimeId.Require(secret.Value, nameof(secret));
            Secret = secret;
        }

        public SecretRef Secret { get; }
    }

    /// <summary>
    /// References a vault-owned OAuth session containing refreshable token state.
    /// The durable provider definition never contains the access or refresh token.
    /// </summary>
    public sealed record OAuth : AiProviderAuthentication
    {
        [JsonConstructor]
        public OAuth(SecretRef session, AiProviderOAuthFlow flow)
        {
            RuntimeId.Require(session.Value, nameof(session));
            if (!Enum.IsDefined(flow))
            {
                throw new ArgumentOutOfRangeException(nameof(flow), flow, null);
            }

            Session = session;
            Flow = flow;
        }

        public SecretRef Session { get; }

        public AiProviderOAuthFlow Flow { get; }
    }

    /// <summary>
    /// Selects the platform AWS credential chain. No AWS credential material is
    /// persisted in the provider profile.
    /// </summary>
    public sealed record AwsCredentialChain : AiProviderAuthentication;
}

public enum AiProviderOAuthFlow
{
    Browser,
    Device,
}
