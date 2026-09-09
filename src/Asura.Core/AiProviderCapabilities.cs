namespace Asura.Core;

/// <summary>
/// Provider/protocol-level capability ceiling. Individual models can expose a
/// narrower surface; callers must not treat these flags as a model entitlement.
/// </summary>
public sealed record AiProviderCapabilities(
    bool SupportsToolCalling,
    bool SupportsToolBatches,
    bool SupportsImageInput,
    bool SupportsReasoning,
    bool SupportsModelDiscovery)
{
    public static AiProviderCapabilities ChatCompletions { get; } = new(
        SupportsToolCalling: true,
        SupportsToolBatches: true,
        SupportsImageInput: true,
        SupportsReasoning: false,
        SupportsModelDiscovery: true);

    public static AiProviderCapabilities Responses { get; } = new(
        SupportsToolCalling: true,
        SupportsToolBatches: true,
        SupportsImageInput: true,
        SupportsReasoning: true,
        SupportsModelDiscovery: true);
}
