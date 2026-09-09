using System.Collections.Immutable;
using Asura.Core;

namespace Asura.Agent.Providers;

/// <summary>
/// The effort levels the current adapters can translate without silently
/// reinterpreting a user choice. Provider capability flags are only ceilings;
/// this policy narrows them for the selected model and wire route.
/// </summary>
internal static class AiProviderReasoningPolicy
{
    private static readonly ImmutableArray<AgentReasoningEffort> AutomaticOnly =
        [AgentReasoningEffort.Automatic];
    private static readonly ImmutableArray<AgentReasoningEffort> Responses =
    [
        AgentReasoningEffort.Automatic,
        AgentReasoningEffort.Off,
        AgentReasoningEffort.Low,
        AgentReasoningEffort.Medium,
        AgentReasoningEffort.High,
    ];
    private static readonly ImmutableArray<AgentReasoningEffort> ResponsesExtraHigh =
        [.. Responses, AgentReasoningEffort.ExtraHigh];
    private static readonly ImmutableArray<AgentReasoningEffort> Responses56 =
        [.. Responses, AgentReasoningEffort.ExtraHigh, AgentReasoningEffort.Max];
    private static readonly ImmutableArray<AgentReasoningEffort> ResponsesPro =
    [
        AgentReasoningEffort.Automatic,
        AgentReasoningEffort.Medium,
        AgentReasoningEffort.High,
        AgentReasoningEffort.ExtraHigh,
    ];
    private static readonly ImmutableArray<AgentReasoningEffort> AnthropicAdaptive =
    [
        AgentReasoningEffort.Automatic,
        AgentReasoningEffort.Off,
        AgentReasoningEffort.Low,
        AgentReasoningEffort.Medium,
        AgentReasoningEffort.High,
    ];
    private static readonly ImmutableArray<AgentReasoningEffort> AnthropicAlwaysThinking =
    [
        AgentReasoningEffort.Automatic,
        AgentReasoningEffort.Low,
        AgentReasoningEffort.Medium,
        AgentReasoningEffort.High,
    ];
    private static readonly ImmutableArray<AgentReasoningEffort> AnthropicExtraHigh =
        [.. AnthropicAdaptive, AgentReasoningEffort.ExtraHigh];
    private static readonly ImmutableArray<AgentReasoningEffort> AnthropicMaximum =
        [.. AnthropicAdaptive, AgentReasoningEffort.ExtraHigh, AgentReasoningEffort.Max];
    private static readonly ImmutableArray<AgentReasoningEffort> AnthropicAlwaysThinkingMaximum =
        [.. AnthropicAlwaysThinking, AgentReasoningEffort.ExtraHigh, AgentReasoningEffort.Max];

    public static ImmutableArray<AgentReasoningEffort> SupportedEfforts(
        AiProviderProfile profile) => SupportedEfforts(profile, profile.DefaultModel);

    public static ImmutableArray<AgentReasoningEffort> SupportedEfforts(
        AiProviderProfile profile,
        string modelId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (!profile.IsEnabled
            || !profile.Capabilities.SupportsReasoning
            || !AiProviderCatalog.Get(profile.Identity).IsRuntimeSupported)
        {
            return AutomaticOnly;
        }

        return profile.Protocol switch
        {
            AiProviderProtocol.OpenAiResponses => ResponsesEfforts(modelId),
            AiProviderProtocol.GitHubCopilot
                when AiProviderFactory.UsesGitHubCopilotResponses(modelId) =>
                ResponsesEfforts(modelId),
            AiProviderProtocol.AnthropicMessages
                when RejectsDisabledThinking(modelId) =>
                AnthropicAlwaysThinkingMaximum,
            AiProviderProtocol.AnthropicMessages
                when SupportsNativeExtraHighThinking(modelId) =>
                AnthropicMaximum,
            AiProviderProtocol.AnthropicMessages
                when SupportsAdaptiveThinking(modelId) =>
                AnthropicExtraHigh,
            _ => AutomaticOnly,
        };
    }

    private static ImmutableArray<AgentReasoningEffort> ResponsesEfforts(string modelId)
    {
        var normalized = modelId.ToLowerInvariant();
        if (normalized.Contains("gpt-5.6", StringComparison.Ordinal))
        {
            return Responses56;
        }

        if (normalized.Contains("gpt-5.2-pro", StringComparison.Ordinal)
            || normalized.Contains("gpt-5.5-pro", StringComparison.Ordinal))
        {
            return ResponsesPro;
        }

        if (normalized.Contains("gpt-5-pro", StringComparison.Ordinal))
        {
            return [AgentReasoningEffort.Automatic, AgentReasoningEffort.High];
        }

        if (normalized.Contains("gpt-5", StringComparison.Ordinal))
        {
            return ResponsesExtraHigh;
        }

        return Responses;
    }

    public static bool SupportsAdaptiveThinking(string modelId)
    {
        var normalized = modelId.ToLowerInvariant();
        return normalized.Contains("claude-opus-4-6", StringComparison.Ordinal)
            || normalized.Contains("claude-opus-4-7", StringComparison.Ordinal)
            || normalized.Contains("claude-opus-4-8", StringComparison.Ordinal)
            || normalized.Contains("claude-sonnet-4-6", StringComparison.Ordinal)
            || normalized.Contains("claude-opus-5", StringComparison.Ordinal)
            || normalized.Contains("claude-sonnet-5", StringComparison.Ordinal)
            || normalized.Contains("claude-fable-5", StringComparison.Ordinal)
            || normalized.Contains("claude-mythos", StringComparison.Ordinal);
    }

    public static bool SupportsSummarizedThinking(string modelId)
    {
        var normalized = modelId.ToLowerInvariant();
        return normalized.Contains("claude-opus-4-7", StringComparison.Ordinal)
            || normalized.Contains("claude-opus-4-8", StringComparison.Ordinal)
            || normalized.Contains("claude-opus-5", StringComparison.Ordinal)
            || normalized.Contains("claude-sonnet-5", StringComparison.Ordinal)
            || normalized.Contains("claude-fable-5", StringComparison.Ordinal)
            || normalized.Contains("claude-mythos", StringComparison.Ordinal);
    }

    public static bool SupportsNativeExtraHighThinking(string modelId)
    {
        var normalized = modelId.ToLowerInvariant();
        return normalized.Contains("claude-opus-4-7", StringComparison.Ordinal)
            || normalized.Contains("claude-opus-4-8", StringComparison.Ordinal)
            || normalized.Contains("claude-opus-5", StringComparison.Ordinal)
            || normalized.Contains("claude-sonnet-5", StringComparison.Ordinal);
    }

    public static bool RejectsDisabledThinking(string modelId)
    {
        var normalized = modelId.ToLowerInvariant();
        return normalized.Contains("claude-fable-", StringComparison.Ordinal)
            || normalized.Contains("claude-mythos-", StringComparison.Ordinal);
    }
}
