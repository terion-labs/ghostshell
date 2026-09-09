namespace Asura.Application;

public sealed record AgentWebSearchResult : AgentWebToolResult
{
    public const int MaximumTitleBytes = 1_024;

    public AgentWebSearchResult(
        string finalUrl,
        string title,
        IReadOnlyList<AgentWebSearchEntry> entries,
        bool truncated)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count is < 1 or > AgentWebSearchRequest.MaximumResultCount
            || entries.Any(static entry => entry is null))
        {
            throw new ArgumentException(
                $"A web search result must contain 1 to {AgentWebSearchRequest.MaximumResultCount} entries.",
                nameof(entries));
        }

        FinalUrl = RequireFinalUrl(finalUrl);
        Title = RequireBoundedText(title, MaximumTitleBytes, nameof(title));
        Entries = [.. entries];
        Truncated = truncated;
    }

    public string FinalUrl { get; }

    public string Title { get; }

    public IReadOnlyList<AgentWebSearchEntry> Entries { get; }

    public bool Truncated { get; }
}

public enum AgentWebSearchErrorCode
{
    Unavailable,
    NavigationDenied,
    LoadFailed,
    Interstitial,
    ExtractionFailed,
    TimedOut,
    Cancelled,
}

public abstract record AgentWebSearchExecutionResult
{
    private AgentWebSearchExecutionResult()
    {
    }

    public sealed record Succeeded(AgentWebSearchResult Result)
        : AgentWebSearchExecutionResult;

    public sealed record Failed(AgentWebSearchErrorCode Code)
        : AgentWebSearchExecutionResult;
}
