namespace Asura.Agent.Providers;

public sealed class AiProviderRuntimeLimits
{
    public AiProviderRuntimeLimits(
        int maximumRequestBytes = 4 * 1024 * 1024,
        int maximumModelResponseBytes = 2 * 1024 * 1024,
        int maximumStreamResponseBytes = 16 * 1024 * 1024,
        int maximumSseEventBytes = 1024 * 1024,
        int maximumSseEvents = 16 * 1024,
        int maximumProviderFragmentBytes = 8 * 1024,
        int maximumModels = 512,
        int maximumOutputTokens = 4 * 1024,
        TimeSpan? discoveryTimeout = null,
        TimeSpan? streamTimeout = null)
    {
        MaximumRequestBytes = RequireInRange(
            maximumRequestBytes,
            1024,
            32 * 1024 * 1024,
            nameof(maximumRequestBytes));
        MaximumModelResponseBytes = RequireInRange(
            maximumModelResponseBytes,
            1024,
            16 * 1024 * 1024,
            nameof(maximumModelResponseBytes));
        MaximumStreamResponseBytes = RequireInRange(
            maximumStreamResponseBytes,
            1024,
            128 * 1024 * 1024,
            nameof(maximumStreamResponseBytes));
        MaximumSseEventBytes = RequireInRange(
            maximumSseEventBytes,
            256,
            MaximumStreamResponseBytes,
            nameof(maximumSseEventBytes));
        MaximumSseEvents = RequireInRange(
            maximumSseEvents,
            2,
            128 * 1024,
            nameof(maximumSseEvents));
        MaximumProviderFragmentBytes = RequireInRange(
            maximumProviderFragmentBytes,
            1,
            MaximumSseEventBytes,
            nameof(maximumProviderFragmentBytes));
        MaximumModels = RequireInRange(
            maximumModels,
            1,
            4 * 1024,
            nameof(maximumModels));
        MaximumOutputTokens = RequireInRange(
            maximumOutputTokens,
            1,
            1024 * 1024,
            nameof(maximumOutputTokens));
        DiscoveryTimeout = RequireTimeout(
            discoveryTimeout ?? TimeSpan.FromSeconds(20),
            nameof(discoveryTimeout));
        StreamTimeout = RequireTimeout(
            streamTimeout ?? TimeSpan.FromMinutes(2),
            nameof(streamTimeout));
    }

    public static AiProviderRuntimeLimits Default { get; } = new();

    public int MaximumRequestBytes { get; }

    public int MaximumModelResponseBytes { get; }

    public int MaximumStreamResponseBytes { get; }

    public int MaximumSseEventBytes { get; }

    public int MaximumSseEvents { get; }

    public int MaximumProviderFragmentBytes { get; }

    public int MaximumModels { get; }

    public int MaximumOutputTokens { get; }

    public TimeSpan DiscoveryTimeout { get; }

    public TimeSpan StreamTimeout { get; }

    private static int RequireInRange(
        int value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"The value must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static TimeSpan RequireTimeout(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.FromMilliseconds(100) || value > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The timeout must be between 100 milliseconds and 30 minutes.");
        }

        return value;
    }
}
