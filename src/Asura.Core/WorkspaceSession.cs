namespace Asura.Core;

public sealed class WorkspaceSession
{
    public WorkspaceSession(WorkspaceSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public WorkspaceSnapshot Snapshot { get; private set; }

    public void ActivateTab(TabId tabId)
    {
        if (Snapshot.Tabs.All(tab => tab.Id != tabId))
        {
            throw new ArgumentOutOfRangeException(nameof(tabId), tabId, "The tab does not belong to this workspace.");
        }

        var tabs = Snapshot.Tabs
            .Select(tab => tab with { IsActive = tab.Id == tabId })
            .ToArray();

        Snapshot = Snapshot with { Tabs = tabs };
    }
}

