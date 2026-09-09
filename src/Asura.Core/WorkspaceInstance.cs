using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Asura.Core;

public sealed class WorkspaceInstance
{
    /// <summary>
    /// Maximum number of panels in one live workspace. This bound keeps the
    /// complete workspace graph and provider tool schemas finite without
    /// omitting panels from an otherwise valid workspace.
    /// </summary>
    public const int MaximumPanelCount = 64;

    public WorkspaceInstance(
        WorkspaceInstanceId id,
        string title,
        IEnumerable<TabInstance> tabs,
        TabInstanceId activeTabId)
        : this(
            id,
            title,
            [.. (tabs ?? throw new ArgumentNullException(nameof(tabs)))],
            activeTabId)
    {
    }

    [JsonConstructor]
    public WorkspaceInstance(
        WorkspaceInstanceId id,
        string title,
        IReadOnlyList<TabInstance> tabs,
        TabInstanceId activeTabId)
    {
        RuntimeInstanceValidation.RequireId(id.Value, nameof(id));
        ArgumentNullException.ThrowIfNull(tabs);

        var tabCopies = tabs
            .Select(tab => new TabInstance(
                tab ?? throw new ArgumentException(
                    "A workspace cannot contain a null tab.",
                    nameof(tabs))))
            .ToArray();
        if (tabCopies.Length == 0)
        {
            throw new ArgumentException("A workspace must contain at least one tab.", nameof(tabs));
        }

        var panelCount = tabCopies.Sum(tab => (long)tab.Panels.Count);
        if (panelCount > MaximumPanelCount)
        {
            throw new ArgumentException(
                $"A workspace cannot contain more than {MaximumPanelCount} panels.",
                nameof(tabs));
        }

        RuntimeInstanceValidation.RequireUniqueIds(
            tabCopies.Select(tab => tab.Id.Value),
            "A workspace cannot contain duplicate tab IDs.",
            nameof(tabs));
        RuntimeInstanceValidation.RequireUniqueIds(
            tabCopies.SelectMany(tab => tab.Panels).Select(panel => panel.Id.Value),
            "Panel IDs must be unique across the workspace.",
            nameof(tabs));
        if (tabCopies.All(tab => tab.Id != activeTabId))
        {
            throw new ArgumentException(
                "The active tab must belong to the workspace.",
                nameof(activeTabId));
        }

        Id = id;
        Title = RuntimeInstanceValidation.RequireTitle(title, nameof(title));
        Tabs = new ReadOnlyCollection<TabInstance>(tabCopies);
        ActiveTabId = activeTabId;
    }

    public WorkspaceInstance(WorkspaceInstance source)
        : this(
            (source ?? throw new ArgumentNullException(nameof(source))).Id,
            source.Title,
            source.Tabs,
            source.ActiveTabId)
    {
    }

    public WorkspaceInstanceId Id { get; }

    public string Title { get; }

    public IReadOnlyList<TabInstance> Tabs { get; }

    public TabInstanceId ActiveTabId { get; }

    public WorkspaceInstance ActivateTab(TabInstanceId tabId)
    {
        if (Tabs.All(tab => tab.Id != tabId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tabId),
                tabId,
                "The tab does not belong to this workspace.");
        }

        return tabId == ActiveTabId
            ? this
            : new WorkspaceInstance(Id, Title, Tabs, tabId);
    }

    public WorkspaceInstance ActivatePanel(TabInstanceId tabId, PanelInstanceId panelId)
    {
        var tabIndex = -1;
        for (var index = 0; index < Tabs.Count; index++)
        {
            if (Tabs[index].Id == tabId)
            {
                tabIndex = index;
                break;
            }
        }

        if (tabIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tabId),
                tabId,
                "The tab does not belong to this workspace.");
        }

        var currentTab = Tabs[tabIndex];
        var activatedTab = currentTab.ActivatePanel(panelId);
        if (ActiveTabId == tabId && ReferenceEquals(currentTab, activatedTab))
        {
            return this;
        }

        var tabs = Tabs.ToArray();
        tabs[tabIndex] = activatedTab;
        return new WorkspaceInstance(Id, Title, tabs, tabId);
    }

    public WorkspaceInstance ReplacePanelSession(
        TabInstanceId tabId,
        PanelInstanceId panelId,
        SessionId? sessionId)
    {
        var tabIndex = -1;
        for (var index = 0; index < Tabs.Count; index++)
        {
            if (Tabs[index].Id == tabId)
            {
                tabIndex = index;
                break;
            }
        }

        if (tabIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tabId),
                tabId,
                "The tab does not belong to this workspace.");
        }

        var currentTab = Tabs[tabIndex];
        var replacement = currentTab.ReplacePanelSession(panelId, sessionId);
        if (ReferenceEquals(currentTab, replacement))
        {
            return this;
        }

        var tabs = Tabs.ToArray();
        tabs[tabIndex] = replacement;
        return new WorkspaceInstance(Id, Title, tabs, ActiveTabId);
    }
}
