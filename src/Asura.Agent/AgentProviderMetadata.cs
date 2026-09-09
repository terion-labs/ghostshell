namespace Asura.Agent;

/// <summary>
/// Provider-reported token accounting for one committed assistant generation.
/// Cached-input and reasoning tokens are subsets of input/output respectively;
/// they are retained for presentation and accounting but are not added twice to
/// <see cref="TotalTokens"/>.
/// </summary>
public sealed record AgentTokenUsage
{
    public const long MaximumTokenCount = 1_000_000_000_000;

    public AgentTokenUsage(
        long inputTokens,
        long outputTokens,
        long cachedInputTokens = 0,
        long reasoningTokens = 0)
    {
        InputTokens = RequireTokenCount(inputTokens, nameof(inputTokens));
        OutputTokens = RequireTokenCount(outputTokens, nameof(outputTokens));
        CachedInputTokens = RequireTokenCount(
            cachedInputTokens,
            nameof(cachedInputTokens));
        ReasoningTokens = RequireTokenCount(
            reasoningTokens,
            nameof(reasoningTokens));
        if (CachedInputTokens > InputTokens)
        {
            throw new ArgumentException(
                "Cached input tokens cannot exceed total input tokens.",
                nameof(cachedInputTokens));
        }

        if (ReasoningTokens > OutputTokens)
        {
            throw new ArgumentException(
                "Reasoning tokens cannot exceed total output tokens.",
                nameof(reasoningTokens));
        }

        TotalTokens = checked(InputTokens + OutputTokens);
        if (TotalTokens > MaximumTokenCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputTokens),
                "Total token usage exceeds the supported bound.");
        }
    }

    public long InputTokens { get; }

    public long OutputTokens { get; }

    public long CachedInputTokens { get; }

    public long ReasoningTokens { get; }

    public long TotalTokens { get; }

    private static long RequireTokenCount(long value, string parameterName)
    {
        if (value is < 0 or > MaximumTokenCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Token usage must be between 0 and {MaximumTokenCount}.");
        }

        return value;
    }
}
