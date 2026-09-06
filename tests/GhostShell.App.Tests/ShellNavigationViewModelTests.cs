using GhostShell.App.ViewModels;

namespace GhostShell.App.Tests;

public sealed class ShellNavigationViewModelTests
{
    [Fact]
    public void Initial_state_exposes_only_the_workspace_canvas()
    {
        var navigation = new ShellNavigationViewModel();

        Assert.Equal(ShellRoute.Workspace, navigation.Route);
        Assert.Equal(SettingsPage.Appearance, navigation.SettingsPage);
        Assert.Equal(ShellOverlay.None, navigation.Overlay);
        Assert.True(navigation.IsWorkspaceVisible);
        Assert.True(navigation.IsWorkspaceCanvasVisible);
        Assert.False(navigation.IsSettingsVisible);
        Assert.False(navigation.HasOverlay);
    }

    [Theory]
    [InlineData(SettingsPage.Appearance)]
    [InlineData(SettingsPage.Networking)]
    [InlineData(SettingsPage.Files)]
    [InlineData(SettingsPage.Mcp)]
    public void Settings_navigation_dismisses_an_overlay_and_projects_one_subroute(
        SettingsPage page)
    {
        var navigation = new ShellNavigationViewModel();
        navigation.ShowOverlay(ShellOverlay.CommandPalette);

        navigation.ShowSettings(page);

        Assert.Equal(ShellRoute.Settings, navigation.Route);
        Assert.Equal(page, navigation.SettingsPage);
        Assert.Equal(ShellOverlay.None, navigation.Overlay);
        Assert.True(navigation.IsSettingsVisible);
        Assert.False(navigation.IsWorkspaceVisible);
        Assert.False(navigation.HasOverlay);
        Assert.Equal(1, VisibleSettingsPageCount(navigation));
    }

    [Fact]
    public void Mutation_completion_closes_its_unchanged_transient_overlay()
    {
        var navigation = new ShellNavigationViewModel();
        navigation.ShowSettings(SettingsPage.Files);
        navigation.ShowOverlay(ShellOverlay.CommandPalette);
        var initiatingState = navigation.CaptureRuntimeMutation();

        navigation.CompleteRuntimeMutation(initiatingState);

        Assert.Equal(ShellRoute.Workspace, navigation.Route);
        Assert.Equal(ShellOverlay.None, navigation.Overlay);
        Assert.True(navigation.IsWorkspaceCanvasVisible);
    }

    [Fact]
    public void Mutation_completion_does_not_steal_a_newer_settings_route()
    {
        var navigation = new ShellNavigationViewModel();
        navigation.ShowOverlay(ShellOverlay.CommandPalette);
        var initiatingState = navigation.CaptureRuntimeMutation();
        navigation.ShowSettings(SettingsPage.Files);

        navigation.CompleteRuntimeMutation(initiatingState);

        Assert.Equal(ShellRoute.Settings, navigation.Route);
        Assert.Equal(SettingsPage.Files, navigation.SettingsPage);
        Assert.Equal(ShellOverlay.None, navigation.Overlay);
    }

    [Fact]
    public void Overlay_revision_prevents_completion_from_closing_a_newer_overlay()
    {
        var navigation = new ShellNavigationViewModel();
        navigation.ShowOverlay(ShellOverlay.CommandPalette);
        var initiatingState = navigation.CaptureRuntimeMutation();
        navigation.ShowOverlay(ShellOverlay.NewPanel);
        navigation.ShowOverlay(ShellOverlay.CommandPalette);

        navigation.CompleteRuntimeMutation(initiatingState);

        Assert.Equal(ShellRoute.Workspace, navigation.Route);
        Assert.Equal(ShellOverlay.CommandPalette, navigation.Overlay);
        Assert.False(navigation.IsWorkspaceCanvasVisible);
    }

    [Theory]
    [InlineData(ShellOverlay.LayoutDesigner)]
    [InlineData(ShellOverlay.DefinitionEditor)]
    public void Editor_overlays_block_runtime_navigation_restoration(
        ShellOverlay editorOverlay)
    {
        var navigation = new ShellNavigationViewModel();
        navigation.ShowSettings(SettingsPage.Files);
        var initiatingState = navigation.CaptureRuntimeMutation();
        navigation.ShowOverlay(editorOverlay);

        navigation.CompleteRuntimeMutation(initiatingState);

        Assert.Equal(ShellRoute.Settings, navigation.Route);
        Assert.Equal(editorOverlay, navigation.Overlay);
    }

    private static int VisibleSettingsPageCount(ShellNavigationViewModel navigation) =>
        new[]
        {
            navigation.IsAppearanceSettingsVisible,
            navigation.IsWorkspaceSettingsVisible,
            navigation.IsNetworkingSettingsVisible,
            navigation.IsKeybindingSettingsVisible,
            navigation.IsFilesSettingsVisible,
            navigation.IsBrowserSettingsVisible,
            navigation.IsTerminalSettingsVisible,
            navigation.IsQuickTerminalSettingsVisible,
            navigation.IsSecretsSettingsVisible,
            navigation.IsDiagnosticsSettingsVisible,
            navigation.IsAgentSettingsVisible && !navigation.IsMcpSettingsVisible,
            navigation.IsMcpSettingsVisible,
            navigation.IsAboutSettingsVisible,
        }.Count(isVisible => isVisible);
}
