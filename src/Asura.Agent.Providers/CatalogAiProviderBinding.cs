using Asura.Agent;
using Asura.Core;

namespace Asura.Agent.Providers;

/// <summary>
/// Immutable, non-secret run binding for one stored provider revision. The
/// captured profile contains only an opaque credential reference; secret
/// material remains resolved inside the request-scoped transport.
/// </summary>
public sealed class CatalogAiProviderBinding
{
    private readonly CatalogAiProviderRuntime _owner;
    private readonly AiProviderProfile _profile;

    internal CatalogAiProviderBinding(
        CatalogAiProviderRuntime owner,
        AiProviderProfile profile,
        long revision)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ProfileId = profile.Id;
        Revision = revision;
    }

    public AiProviderProfileId ProfileId { get; }

    public long Revision { get; }

    public string DefaultModel => _profile.DefaultModel;

    public bool IsCurrent => _owner.IsCurrent(this);

    public IAgentProvider CreateProvider() =>
        _owner.CreateProvider(this, _profile);

    public IAgentProvider CreateProvider(string? model) =>
        _owner.CreateProvider(this, _profile, model);

    public IAgentProvider CreateProvider(
        string? model,
        AgentServiceTier serviceTier) =>
        _owner.CreateProvider(this, _profile, model, serviceTier);

    public IAgentProvider CreateProvider(string? model, Uri networkProxy) =>
        _owner.CreateProvider(
            this,
            _profile,
            model,
            AgentServiceTier.Automatic,
            networkProxy);

    public IAgentProvider CreateProvider(
        string? model,
        AgentServiceTier serviceTier,
        Uri networkProxy) =>
        _owner.CreateProvider(this, _profile, model, serviceTier, networkProxy);
}
