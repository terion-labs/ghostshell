using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Translates runtime view models into the exact identity graph governed by the
/// session host and compares host projections without trusting display labels.
/// </summary>
internal static class RuntimeWorkspaceGraphProjection
{
    public static WorkspaceInstance Capture(RuntimeWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var activeTab = workspace.ActiveTab
            ?? throw new InvalidOperationException(
                "A runtime workspace must have an active tab before registration.");
        return new WorkspaceInstance(
            workspace.Id,
            workspace.Name,
            workspace.Tabs.Select(CaptureTab),
            activeTab.Id);
    }

    /// <summary>
    /// Captures placeholders as host-visible panels. They own layout identity
    /// even though they do not own a session yet.
    /// </summary>
    public static TabInstance CaptureTab(RuntimeTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var panels = tab.Panels.ToArray();
        if (panels.Length == 0)
        {
            throw new InvalidOperationException("A runtime tab must have a panel.");
        }

        return new TabInstance(
            tab.Id,
            tab.Title,
            panels.Select(panel => new PanelInstance(
                panel.Id,
                panel.Kind,
                panel.Title)),
            tab.ActivePanelId ?? panels[0].Id);
    }

    public static bool IntentMatches(
        WorkspaceInstance expected,
        WorkspaceInstance actual) =>
        expected.ActiveTabId == actual.ActiveTabId
        && expected.Tabs.Zip(actual.Tabs).All(pair =>
            pair.First.ActivePanelId == pair.Second.ActivePanelId)
        && TopologyMatches(expected, actual);

    public static bool TopologyMatches(
        WorkspaceInstance expected,
        WorkspaceInstance actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.Id != actual.Id
            || !string.Equals(expected.Title, actual.Title, StringComparison.Ordinal)
            || expected.Tabs.Count != actual.Tabs.Count)
        {
            return false;
        }

        for (var tabIndex = 0; tabIndex < expected.Tabs.Count; tabIndex++)
        {
            var expectedTab = expected.Tabs[tabIndex];
            var actualTab = actual.Tabs[tabIndex];
            if (expectedTab.Id != actualTab.Id
                || !string.Equals(expectedTab.Title, actualTab.Title, StringComparison.Ordinal)
                || expectedTab.Panels.Count != actualTab.Panels.Count)
            {
                return false;
            }

            for (var panelIndex = 0; panelIndex < expectedTab.Panels.Count; panelIndex++)
            {
                var expectedPanel = expectedTab.Panels[panelIndex];
                var actualPanel = actualTab.Panels[panelIndex];
                if (expectedPanel.Id != actualPanel.Id
                    || expectedPanel.Kind != actualPanel.Kind
                    || !string.Equals(
                        expectedPanel.Title,
                        actualPanel.Title,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
