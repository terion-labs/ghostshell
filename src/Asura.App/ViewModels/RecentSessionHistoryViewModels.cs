using Asura.Application;

namespace Asura.App.ViewModels;

public enum HistoryExportScope
{
    AllRetained,
    CurrentResults,
}

public sealed record HistoryRetentionOption
{
    public HistoryRetentionOption(
        string displayName,
        string description,
        RecentSessionRetentionPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        DisplayName = displayName.Trim();
        Description = description.Trim();
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public string DisplayName { get; }

    public string Description { get; }

    public RecentSessionRetentionPolicy Policy { get; }
}
