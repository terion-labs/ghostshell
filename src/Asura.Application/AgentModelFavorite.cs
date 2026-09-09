using Asura.Core;

namespace Asura.Application;

public sealed record AgentModelFavorite
{
    public const int MaximumCount = 256;

    public AgentModelFavorite(AiProviderProfileId providerId, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId.Value, nameof(providerId));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (modelId.Length > AiProviderProfile.MaximumModelIdLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(modelId),
                "A favorite model identifier exceeds the provider-model limit.");
        }

        ProviderId = providerId;
        ModelId = modelId;
    }

    public AiProviderProfileId ProviderId { get; }

    public string ModelId { get; }
}
