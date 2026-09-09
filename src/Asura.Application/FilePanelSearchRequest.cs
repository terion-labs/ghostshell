namespace Asura.Application;

/// <summary>A provider-backed file-name search rooted at one panel location.</summary>
public sealed record FilePanelSearchRequest
{
    public FilePanelSearchRequest(
        FilePanelLocation location,
        string query,
        FilePanelDiscoveryScope scope,
        bool showHidden)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 256 || query.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A file search query must be at most 256 characters and contain no control characters.",
                nameof(query));
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, null);
        }

        Query = query.Trim();
        Scope = scope;
        ShowHidden = showHidden;
    }

    public FilePanelLocation Location { get; }

    public string Query { get; }

    public FilePanelDiscoveryScope Scope { get; }

    public bool ShowHidden { get; }
}
