using System.Collections.Immutable;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Providers;

internal static class AiProviderServiceTierPolicy
{
    private static readonly ImmutableArray<AgentServiceTier> None = [];
    private static readonly ImmutableArray<AgentServiceTier> OpenAi =
    [
        AgentServiceTier.Automatic,
        AgentServiceTier.Default,
        AgentServiceTier.Flex,
        AgentServiceTier.Priority,
    ];
    private static readonly ImmutableArray<AgentServiceTier> XAi =
    [
        AgentServiceTier.Default,
        AgentServiceTier.Priority,
    ];

    public static ImmutableArray<AgentServiceTier> SupportedTiers(
        AiProviderProfile profile,
        string modelId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (!profile.IsEnabled || !AiProviderCatalog.Get(profile.Identity).IsRuntimeSupported)
        {
            return None;
        }

        if (profile.Identity == AiProviderKind.OpenAi
            && profile.Protocol == AiProviderProtocol.OpenAiResponses
            && profile.Authentication is AiProviderAuthentication.ApiKey)
        {
            return OpenAi;
        }

        if (profile.Identity == AiProviderKind.XAi
            && profile.Protocol == AiProviderProtocol.OpenAiChatCompletions
            && (modelId.Contains("grok-4.5", StringComparison.OrdinalIgnoreCase)
                || modelId.Contains("grok-4.6", StringComparison.OrdinalIgnoreCase)))
        {
            return XAi;
        }

        return None;
    }

    public static void EnsureSupported(
        AiProviderProfile profile,
        string modelId,
        AgentServiceTier serviceTier)
    {
        if (!Enum.IsDefined(serviceTier))
        {
            throw new ArgumentOutOfRangeException(nameof(serviceTier));
        }

        var supported = SupportedTiers(profile, modelId);
        if (serviceTier == AgentServiceTier.Automatic && supported.IsEmpty)
        {
            return;
        }

        if (!supported.Contains(serviceTier))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }
    }
}
