namespace Asura.Application;

public sealed record AgentWebSearchEntry
{
    public const int MaximumTitleBytes = 1 * 1_024;
    public const int MaximumDescriptionBytes = 4 * 1_024;

    public AgentWebSearchEntry(string url, string title, string description)
    {
        var boundedUrl = AgentWebToolResult.RequireBoundedText(
            url,
            AgentWebToolRequest.MaximumUrlBytes,
            nameof(url));
        if (!Uri.TryCreate(boundedUrl, UriKind.Absolute, out var address)
            || !(address.Scheme.Equals(
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase)
                || address.Scheme.Equals(
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(address.Host)
            || !string.IsNullOrEmpty(address.UserInfo))
        {
            throw new ArgumentException(
                "A search result URL must be bounded, credential-free HTTP(S).",
                nameof(url));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Url = address.AbsoluteUri;
        Title = AgentWebToolResult.RequireBoundedText(
            title,
            MaximumTitleBytes,
            nameof(title));
        Description = AgentWebToolResult.RequireBoundedText(
            description,
            MaximumDescriptionBytes,
            nameof(description));
    }

    public string Url { get; }

    public string Title { get; }

    public string Description { get; }
}
