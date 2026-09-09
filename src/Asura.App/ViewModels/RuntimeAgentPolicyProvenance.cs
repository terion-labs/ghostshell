using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Immutable effective policy captured with a live runtime graph. Definition
/// changes can create new provenance values but cannot rewrite an accepted tab.
/// </summary>
public sealed record RuntimeAgentPolicyProvenance
{
    public RuntimeAgentPolicyProvenance(
        AgentPolicy? effectivePolicy,
        IEnumerable<Source>? sources = null,
        bool hasPolicyOverride = false)
    {
        if (effectivePolicy is not null
            && !effectivePolicy.IsValidForDurableStorage())
        {
            throw new ArgumentException(
                "Runtime policy provenance requires a durable baseline policy.",
                nameof(effectivePolicy));
        }

        var copiedSources = sources?.ToArray() ?? [];
        if (copiedSources.Any(source => source is null)
            || copiedSources.Length > 2
            || copiedSources.Distinct().Count() != copiedSources.Length
            || copiedSources
                .GroupBy(source => source.Definition.Kind)
                .Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Runtime policy provenance can contain one workspace and one screen source.",
                nameof(sources));
        }

        var normalizedPolicy = effectivePolicy is null
            ? null
            : AgentPolicyResolver.Resolve(effectivePolicy);
        if (hasPolicyOverride
            && (normalizedPolicy is null || copiedSources.Length == 0))
        {
            throw new ArgumentException(
                "A durable runtime policy override requires definition provenance.",
                nameof(hasPolicyOverride));
        }

        EffectivePolicy = normalizedPolicy;
        Sources = Array.AsReadOnly(copiedSources);
        HasPolicyOverride = hasPolicyOverride;
    }

    public static RuntimeAgentPolicyProvenance Unconfigured { get; } =
        new(effectivePolicy: null);

    public AgentPolicy? EffectivePolicy { get; }

    public IReadOnlyList<Source> Sources { get; }

    /// <summary>
    /// True only when at least one captured definition supplied an explicit
    /// durable policy. Definition lineage alone does not select an endpoint.
    /// </summary>
    public bool HasPolicyOverride { get; }

    public RuntimeAgentPolicyProvenance WithOverride(
        AgentPolicy? policy,
        DefinitionKey source,
        long revision)
    {
        return new(
            policy is null
                ? EffectivePolicy
                : EffectivePolicy is null
                    ? AgentPolicyResolver.Resolve(policy)
                    : AgentPolicyResolver.Resolve(EffectivePolicy, screen: policy),
            Sources.Append(new Source(source, revision)),
            hasPolicyOverride: HasPolicyOverride || policy is not null);
    }

    public sealed record Source
    {
        public Source(DefinitionKey definition, long revision)
        {
            if (definition.Kind != ScreenDefinition.Kind
                && definition.Kind != WorkspaceDefinition.Kind)
            {
                throw new ArgumentException(
                    "Runtime policy sources must be saved screens or workspaces.",
                    nameof(definition));
            }

            if (revision <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(revision));
            }

            Definition = definition;
            Revision = revision;
        }

        public DefinitionKey Definition { get; }

        public long Revision { get; }
    }
}
