using Asura.Core;

namespace Asura.Core.Tests;

public sealed class ThemeChromePreferenceTests
{
    private static ThemePreference Theme(
        InterfaceDensity density = InterfaceDensity.Cozy,
        bool showTabBar = true,
        bool showWorkspacesPanel = true,
        TabStripPlacement tabPlacement = TabStripPlacement.Top,
        WorkspacePanelPlacement workspacePanelPlacement = WorkspacePanelPlacement.Left) =>
        new(
            new ThemePreferenceId("builtin.theme.automatic"),
            "Automatic",
            AppearanceMode.Dark,
            PlatformProfile.Automatic,
            AccentPreference.FollowHost,
            null,
            density,
            showTabBar,
            showWorkspacesPanel,
            tabPlacement,
            workspacePanelPlacement);

    private static HostAppearance Host(
        bool reducedTransparency = false,
        bool highContrast = false) =>
        new(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null,
            highContrast: highContrast,
            reducedTransparency: reducedTransparency);

    [Fact]
    public void Chrome_settings_default_to_an_unmodified_window()
    {
        var theme = ThemePreference.Default;

        Assert.Equal(InterfaceDensity.Cozy, theme.Density);
        Assert.True(theme.ShowTabBar);
        Assert.True(theme.ShowWorkspacesPanel);
        Assert.Equal(TabStripPlacement.Top, theme.TabStripPlacement);
        Assert.Equal(WorkspacePanelPlacement.Left, theme.WorkspacePanelPlacement);
    }

    [Fact]
    public void MacOS_first_run_defaults_to_normal_translucent_glass()
    {
        var theme = ThemePreference.DefaultFor(HostOperatingSystem.MacOS);

        Assert.Equal(InterfaceDensity.Cozy, theme.Density);
        Assert.True(theme.IsTranslucent);
        Assert.True(theme.HasGlassPanels);
        Assert.False(theme.OverridesBackdropOpacity);
        Assert.Equal(78, theme.BackdropOpacityPercent);
    }

    [Theory]
    [InlineData(HostOperatingSystem.Windows)]
    [InlineData(HostOperatingSystem.Linux)]
    public void Other_platforms_keep_the_existing_first_run_default(
        HostOperatingSystem operatingSystem)
    {
        Assert.Same(ThemePreference.Default, ThemePreference.DefaultFor(operatingSystem));
    }

    [Fact]
    public void Resolving_carries_every_chrome_setting_through()
    {
        var resolved = Theme(
                density: InterfaceDensity.Compact,
                showTabBar: false,
                showWorkspacesPanel: false,
                tabPlacement: TabStripPlacement.Bottom,
                workspacePanelPlacement: WorkspacePanelPlacement.Right)
            .Resolve(Host());

        Assert.Equal(InterfaceDensity.Compact, resolved.Density);
        Assert.False(resolved.ShowTabBar);
        Assert.False(resolved.ShowWorkspacesPanel);
        Assert.Equal(TabStripPlacement.Bottom, resolved.TabStripPlacement);
        Assert.Equal(WorkspacePanelPlacement.Right, resolved.WorkspacePanelPlacement);
    }

    [Fact]
    public void Reduced_transparency_does_not_touch_layout_settings()
    {
        var resolved = Theme(
                density: InterfaceDensity.Comfortable,
                tabPlacement: TabStripPlacement.Bottom)
            .Resolve(Host(reducedTransparency: true));

        Assert.Equal(InterfaceDensity.Comfortable, resolved.Density);
        Assert.Equal(TabStripPlacement.Bottom, resolved.TabStripPlacement);
    }
}

public sealed class TabStripPlacementTests
{
    private static ThemePreference Theme(TabStripPlacement placement) => new(
        new ThemePreferenceId("builtin.theme.automatic"),
        "Automatic",
        AppearanceMode.Dark,
        PlatformProfile.Automatic,
        AccentPreference.FollowHost,
        tabStripPlacement: placement);

    [Theory]
    [InlineData(TabStripPlacement.Top)]
    [InlineData(TabStripPlacement.Bottom)]
    [InlineData(TabStripPlacement.Left)]
    [InlineData(TabStripPlacement.Right)]
    public void Every_edge_round_trips(TabStripPlacement placement)
    {
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null);

        Assert.Equal(placement, Theme(placement).Resolve(host).TabStripPlacement);
    }

    [Fact]
    public void All_four_edges_are_offered() =>
        Assert.Equal(
            [
                TabStripPlacement.Top,
                TabStripPlacement.Bottom,
                TabStripPlacement.Left,
                TabStripPlacement.Right,
            ],
            Enum.GetValues<TabStripPlacement>());

    [Fact]
    public void An_unknown_edge_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Theme((TabStripPlacement)42));
}
