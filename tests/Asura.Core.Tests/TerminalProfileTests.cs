namespace Asura.Core.Tests;

public sealed class TerminalProfileTests
{
    [Fact]
    public void Terminal_palette_remains_independent_of_application_appearance()
    {
        var profile = new TerminalProfile(
            new TerminalProfileId("operator"),
            "Operator",
            "JetBrains Mono",
            13,
            1.2,
            TerminalCursorStyle.Block,
            cursorBlink: false,
            100_000,
            TerminalPalette.AsuraDark,
            BuiltInKeymaps.MacOsTerminalId);
        var preference = new ThemePreference(
            new ThemePreferenceId("light"),
            "Light chrome",
            AppearanceMode.Light,
            PlatformProfile.Automatic,
            AccentPreference.FollowHost);

        _ = preference.Resolve(new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Light,
            RgbColor.Parse("#336699")));

        Assert.Same(TerminalPalette.AsuraDark, profile.Palette);
        Assert.Equal(RgbColor.Parse("#111111"), profile.Palette.Background);
        Assert.Equal(16, profile.Palette.AnsiColors.Count);
    }

    [Fact]
    public void Terminal_palette_requires_all_sixteen_ansi_colors()
    {
        var colors = Enumerable.Repeat(RgbColor.Parse("#000000"), 15).ToArray();

        Assert.Throws<ArgumentException>(() => new TerminalPalette(
            "Incomplete",
            RgbColor.Parse("#FFFFFF"),
            RgbColor.Parse("#000000"),
            RgbColor.Parse("#FFFFFF"),
            RgbColor.Parse("#333333"),
            colors));
    }

    [Fact]
    public void Terminal_profile_exposes_a_durable_definition_key()
    {
        var profile = new TerminalProfile(
            new TerminalProfileId("default"),
            "Default",
            "monospace",
            12,
            1,
            TerminalCursorStyle.Bar,
            cursorBlink: true,
            10_000,
            TerminalPalette.AsuraDark,
            BuiltInKeymaps.LinuxTerminalId);

        Assert.Equal(DefinitionKind.TerminalProfile, profile.Key.Kind);
        Assert.Equal("default", profile.Key.Value);
        Assert.Equal(1, profile.SchemaVersion);
    }
}
