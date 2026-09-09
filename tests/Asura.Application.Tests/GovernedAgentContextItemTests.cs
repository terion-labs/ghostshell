using Asura.Core;

namespace Asura.Application.Tests;

public sealed class GovernedAgentContextItemTests
{
    [Fact]
    public void ContextItemCopiesItsOrderedSupportedOperations()
    {
        var operations = new List<string>
        {
            BuiltInAgentTools.TerminalReadScreen,
            BuiltInAgentTools.TerminalWait,
        };

        var item = ContextItem(operations);
        operations.Clear();

        Assert.Equal(
            [
                BuiltInAgentTools.TerminalReadScreen,
                BuiltInAgentTools.TerminalWait,
            ],
            [.. item.SupportedOperations]);
    }

    [Fact]
    public void ContextItemPreservesItsPanelKind()
    {
        var item = new GovernedAgentContextItem(
            new WindowInstanceId("window"),
            new WorkspaceInstanceId("workspace"),
            new TabInstanceId("tab"),
            new PanelInstanceId("browser-panel"),
            new SessionId("browser-session"),
            PanelKind.Browser,
            workspaceTitle: "Workspace",
            tabTitle: "Tab",
            panelTitle: "Browser",
            connectionBoundary: null,
            workingDirectory: null,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            isVisible: true,
            isFocused: true,
            hasActiveWork: false,
            [BuiltInAgentTools.BrowserReadState]);

        Assert.Equal(PanelKind.Browser, item.Kind);
        Assert.Equal(
            [BuiltInAgentTools.BrowserReadState],
            [.. item.SupportedOperations]);
    }

    [Fact]
    public void ContextItemRejectsUnboundedDisplayMetadata()
    {
        var unboundedTitle = new string(
            'x',
            GovernedAgentContextItem.MaximumDisplayTextBytes + 1);

        var error = Assert.Throws<ArgumentException>(
            () => ContextItem(
                [BuiltInAgentTools.TerminalReadScreen],
                panelTitle: unboundedTitle));

        Assert.Equal("panelTitle", error.ParamName);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("hidden\u200Bformat")]
    public void ContextItemRejectsUnsafeDisplayMetadata(string panelTitle)
    {
        var error = Assert.Throws<ArgumentException>(
            () => ContextItem(
                [BuiltInAgentTools.TerminalReadScreen],
                panelTitle));

        Assert.Equal("panelTitle", error.ParamName);
    }

    [Fact]
    public void ContextItemBoundsOperationEnumeration()
    {
        var operations = Enumerable
            .Range(0, GovernedAgentContextItem.MaximumSupportedOperations + 1)
            .Select(index => $"terminal.operation_{index}");

        var error = Assert.Throws<ArgumentException>(
            () => ContextItem(operations));

        Assert.Equal("supportedOperations", error.ParamName);
    }

    [Fact]
    public void File_context_preserves_a_bounded_trusted_scope_display()
    {
        var item = new GovernedAgentContextItem(
            new WindowInstanceId("window"),
            new WorkspaceInstanceId("workspace"),
            new TabInstanceId("tab"),
            new PanelInstanceId("file-panel"),
            new SessionId("file-session"),
            PanelKind.FileViewer,
            workspaceTitle: "Workspace",
            tabTitle: "Tab",
            panelTitle: "Files",
            connectionBoundary: null,
            workingDirectory: null,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            isVisible: true,
            isFocused: true,
            hasActiveWork: false,
            [BuiltInAgentTools.FilesList],
            fileProviderProfileId: "files.production",
            fileRootDisplay: "/srv/data");

        Assert.Equal("files.production", item.FileProviderProfileId);
        Assert.Equal("/srv/data", item.FileRootDisplay);
    }

    [Fact]
    public void File_context_scope_is_paired_kind_specific_and_non_secret()
    {
        Assert.Throws<ArgumentException>(() =>
            new GovernedAgentContextItem(
                new WindowInstanceId("window"),
                new WorkspaceInstanceId("workspace"),
                new TabInstanceId("tab"),
                new PanelInstanceId("file-panel"),
                new SessionId("file-session"),
                PanelKind.FileViewer,
                null,
                null,
                null,
                null,
                null,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                true,
                true,
                false,
                [BuiltInAgentTools.FilesList],
                fileProviderProfileId: "files.production"));
        Assert.Throws<ArgumentException>(() =>
            new GovernedAgentContextItem(
                new WindowInstanceId("window"),
                new WorkspaceInstanceId("workspace"),
                new TabInstanceId("tab"),
                new PanelInstanceId("terminal-panel"),
                new SessionId("terminal-session"),
                PanelKind.Terminal,
                null,
                null,
                null,
                null,
                null,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                true,
                true,
                false,
                [BuiltInAgentTools.TerminalReadScreen],
                "files.production",
                "/srv/data"));
        Assert.Throws<ArgumentException>(() =>
            new GovernedAgentContextItem(
                new WindowInstanceId("window"),
                new WorkspaceInstanceId("workspace"),
                new TabInstanceId("tab"),
                new PanelInstanceId("file-panel"),
                new SessionId("file-session"),
                PanelKind.FileViewer,
                null,
                null,
                null,
                null,
                null,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                true,
                true,
                false,
                [BuiltInAgentTools.FilesRead],
                "files.production",
                "/password=hunter2"));
    }

    private static GovernedAgentContextItem ContextItem(
        IEnumerable<string> operations,
        string panelTitle = "Terminal") =>
        new(
            new WindowInstanceId("window"),
            new WorkspaceInstanceId("workspace"),
            new TabInstanceId("tab"),
            new PanelInstanceId("panel"),
            new SessionId("session"),
            workspaceTitle: "Workspace",
            tabTitle: "Tab",
            panelTitle,
            connectionBoundary: "Local terminal",
            workingDirectory: "/work",
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            isVisible: true,
            isFocused: true,
            hasActiveWork: false,
            operations);
}
