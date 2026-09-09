using Asura.Agent;
using Asura.Core;

namespace Asura.Agent.Runtime;

/// <summary>
/// Resolves a configured provider profile without exposing provider-specific
/// transport or credential services to the governed agent runtime.
/// </summary>
public interface IAgentProviderResolver
{
    /// <summary>
    /// Pins one immutable, non-secret provider configuration for the run.
    /// </summary>
    IAgentProviderBinding PinProvider(AiProviderProfileId profileId);
}

public interface IAgentProviderBinding
{
    AiProviderProfileId ProfileId { get; }

    long Revision { get; }

    /// <summary>
    /// The exact default model captured with this immutable provider revision.
    /// </summary>
    string DefaultModel { get; }

    /// <summary>
    /// False after the underlying profile is edited, disabled, or removed.
    /// The governed run must then require Clear before sending more transcript.
    /// </summary>
    bool IsCurrent { get; }

    int? ContextWindowTokens(string model) => null;

    /// <summary>
    /// Returns a binding-owned, request-scoped adapter for the exact requested
    /// model. The runtime does not dispose it because a cancellation-fenced
    /// stream may still be unwinding.
    /// </summary>
    IAgentProvider CreateProvider(string model);

    IAgentProvider CreateProvider(
        string model,
        AgentServiceTier serviceTier) =>
        serviceTier == AgentServiceTier.Automatic
            ? CreateProvider(model)
            : throw new ArgumentOutOfRangeException(nameof(serviceTier));
}
