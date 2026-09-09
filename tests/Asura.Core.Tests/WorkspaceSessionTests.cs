namespace Asura.Core.Tests;

public sealed class WorkspaceSessionTests
{
    [Fact]
    public void Sample_workspace_opens_the_saved_screen()
    {
        var workspace = SampleWorkspace.Create();

        Assert.Equal("Production", workspace.Name);
        Assert.Equal("Deploy Dashboard", workspace.ActiveTab.Title);
        Assert.Equal(3, workspace.ActiveTab.Panels.Count);
    }

    [Fact]
    public void Activating_a_tab_changes_the_single_active_tab()
    {
        var session = new WorkspaceSession(SampleWorkspace.Create());

        session.ActivateTab(new TabId("api-server"));

        Assert.Equal("api-server", session.Snapshot.ActiveTab.Title);
        Assert.Single(session.Snapshot.Tabs, tab => tab.IsActive);
    }

    [Fact]
    public void Activating_an_unknown_tab_fails_loudly()
    {
        var session = new WorkspaceSession(SampleWorkspace.Create());

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => session.ActivateTab(new TabId("missing")));

        Assert.Equal("tabId", error.ParamName);
    }
}
