using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.Tests;

public sealed class RuntimeTabIdentityTests
{
    [Theory]
    [InlineData(PanelKind.Terminal, "terminal", Symbol.WindowConsole)]
    [InlineData(PanelKind.Browser, "globe", Symbol.Globe)]
    [InlineData(PanelKind.FileViewer, "folder", Symbol.Folder)]
    [InlineData(PanelKind.Statistics, "pulse", Symbol.PulseSquare)]
    [InlineData(PanelKind.ProcessMonitor, "gauge", Symbol.Gauge)]
    [InlineData(PanelKind.DatabaseViewer, "database", Symbol.Database)]
    [InlineData(PanelKind.Docker, "box", Symbol.Box)]
    [InlineData(PanelKind.Git, "branch", Symbol.Branch)]
    public void First_real_panel_sets_the_initial_tab_icon(
        PanelKind kind,
        string icon,
        Symbol symbol)
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "New tab", "Launcher");

        tab.AddPanel(new TestPanel(kind, "First panel"));

        Assert.Equal(icon, tab.Icon);
        Assert.Equal(symbol, tab.IconSymbol);
    }

    [Fact]
    public void A_chosen_tab_identity_is_not_replaced_by_later_panels()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Browser", "Local");
        tab.AddPanel(new TestPanel(PanelKind.Browser, "Browser"));
        Assert.True(tab.SetIdentity("Research", "star"));

        tab.AddPanel(new TestPanel(PanelKind.DatabaseViewer, "Database"));

        Assert.Equal("Research", tab.Title);
        Assert.Equal("star", tab.Icon);
        Assert.Equal(Symbol.Star, tab.IconSymbol);
    }

    [Fact]
    public void Recovery_preserves_a_chosen_tab_icon()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Browser", "Test");
        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            PanelInstanceId.New(),
            PanelKind.Browser,
            "Browser",
            "BROWSER",
            "Unavailable for this test."));
        Assert.True(tab.SetIdentity("Research", "star"));
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Workspace",
            "Bronze",
            []);
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;

        var json = RuntimeWorkspaceRecoveryCodec.Serialize(workspace);
        var restored = RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            new RuntimeRecoverySnapshot(
                "test-run",
                RuntimeWorkspaceRecoveryCodec.SnapshotKey,
                RuntimeWorkspaceRecoveryCodec.SchemaVersion,
                json,
                DateTimeOffset.UnixEpoch),
            out var payload,
            out var error);

        Assert.True(restored, error);
        var recoveredTab = Assert.Single(payload!.Workspace!.Tabs);
        Assert.Equal("Research", recoveredTab.Title);
        Assert.Equal("star", recoveredTab.Icon);
        Assert.True(recoveredTab.HasChosenTitle);
        Assert.True(recoveredTab.HasChosenIcon);
    }

    private sealed class TestPanel(PanelKind kind, string title)
        : RuntimePanelViewModel(PanelInstanceId.New(), kind, title, kind.ToString());
}
