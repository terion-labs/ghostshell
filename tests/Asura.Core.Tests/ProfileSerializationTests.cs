using System.Text.Json;

namespace Asura.Core.Tests;

public sealed class ProfileSerializationTests
{
    [Theory]
    [InlineData(TerminalPasteSafetyPolicy.ProtectUnsafe)]
    [InlineData(TerminalPasteSafetyPolicy.ProtectUnsafeIncludingBracketed)]
    [InlineData(TerminalPasteSafetyPolicy.AllowUnsafe)]
    public void Theme_terminal_and_keymap_profiles_round_trip_without_losing_unknown_commands(
        TerminalPasteSafetyPolicy legacyPolicy)
    {
        var theme = new ThemePreference(
            new ThemePreferenceId("custom"),
            "Custom",
            AppearanceMode.Dark,
            PlatformProfile.Kde,
            AccentPreference.Custom(RgbColor.Parse("#123456")),
            textScaleOverride: 1.75);
        var terminal = new TerminalProfile(
            new TerminalProfileId("terminal"),
            "Terminal",
            "JetBrains Mono",
            13.5,
            1.2,
            TerminalCursorStyle.Bar,
            cursorBlink: true,
            5_000,
            TerminalPalette.AsuraDark,
            new KeymapProfileId("custom-map"),
            new TerminalClipboardPolicy(
                TerminalClipboardAccess.Deny,
                TerminalClipboardAccess.Ask,
                legacyPolicy),
            TerminalLinkPolicy.Disabled,
            imeEnabled: false,
            TerminalShellIntegrationMode.Fish,
            TerminalBellMode.SystemAndVisual,
            TerminalCompatibilityProfile.Legacy);
        var unknownBinding = new CommandBinding(
            new CommandId("future.command"),
            KeySequence.Of(new KeyStroke("K", KeyModifiers.Control)),
            CommandContext.Terminal,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["future"] = "value" });
        var keymap = new KeymapProfile(
            new KeymapProfileId("custom-map"),
            "Custom map",
            KeymapLayer.Terminal,
            [unknownBinding]);

        var restoredTheme = RoundTrip(theme);
        var restoredTerminal = RoundTrip(terminal);
        var restoredKeymap = RoundTrip(keymap);

        Assert.Equal(theme.Accent, restoredTheme.Accent);
        Assert.Equal(theme.TextScaleOverride, restoredTheme.TextScaleOverride);
        Assert.Equal(terminal.Palette.AnsiColors, restoredTerminal.Palette.AnsiColors);
        Assert.Equal(terminal.FontFamily, restoredTerminal.FontFamily);
        Assert.Equal(terminal.FontSize, restoredTerminal.FontSize);
        Assert.Equal(terminal.LineHeight, restoredTerminal.LineHeight);
        Assert.Equal(terminal.CursorStyle, restoredTerminal.CursorStyle);
        Assert.Equal(terminal.CursorBlink, restoredTerminal.CursorBlink);
        Assert.Equal(terminal.ScrollbackLines, restoredTerminal.ScrollbackLines);
        Assert.Equal(terminal.ClipboardPolicy, restoredTerminal.ClipboardPolicy);
        Assert.Equal(terminal.LinkPolicy, restoredTerminal.LinkPolicy);
        Assert.Equal(terminal.ImeEnabled, restoredTerminal.ImeEnabled);
        Assert.Equal(terminal.ShellIntegration, restoredTerminal.ShellIntegration);
        Assert.Equal(terminal.BellMode, restoredTerminal.BellMode);
        Assert.Equal(terminal.Compatibility, restoredTerminal.Compatibility);
        var restoredBinding = Assert.Single(restoredKeymap.Bindings);
        Assert.Equal(unknownBinding.CommandId, restoredBinding.CommandId);
        Assert.Equal(unknownBinding.Sequence, restoredBinding.Sequence);
        Assert.Equal("value", restoredBinding.Arguments["future"]);
    }

    [Fact]
    public void Schema_one_theme_payload_without_text_scale_override_follows_the_host()
    {
        var legacyJson = JsonSerializer.Serialize(new
        {
            Id = new ThemePreferenceId("legacy-theme"),
            SchemaVersion = 1,
            Name = "Legacy theme",
            Appearance = AppearanceMode.System,
            PlatformProfile = PlatformProfile.Automatic,
            Accent = AccentPreference.FollowHost,
        });

        var restored = JsonSerializer.Deserialize<ThemePreference>(legacyJson)
            ?? throw new InvalidOperationException("Could not deserialize the legacy theme.");
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null,
            textScale: 1.5);

        Assert.Null(restored.TextScaleOverride);
        Assert.Equal(1.5, restored.Resolve(host).TextScale);
    }

    [Fact]
    public void Schema_one_terminal_payload_without_new_settings_uses_compatible_defaults()
    {
        var legacyJson = JsonSerializer.Serialize(new
        {
            Id = new TerminalProfileId("legacy"),
            SchemaVersion = 1,
            Name = "Legacy",
            FontFamily = "monospace",
            FontSize = 12D,
            LineHeight = 1D,
            CursorStyle = TerminalCursorStyle.Block,
            CursorBlink = false,
            ScrollbackLines = 10_000,
            Palette = TerminalPalette.AsuraDark,
            KeymapId = BuiltInKeymaps.MacOsTerminalId,
        });

        var restored = JsonSerializer.Deserialize<TerminalProfile>(legacyJson)
            ?? throw new InvalidOperationException("Could not deserialize the legacy terminal profile.");

        Assert.Equal(1, restored.SchemaVersion);
        Assert.Equal(TerminalClipboardPolicy.Default, restored.ClipboardPolicy);
        Assert.Equal(TerminalLinkPolicy.ConfirmBeforeOpen, restored.LinkPolicy);
        Assert.True(restored.ImeEnabled);
        Assert.Equal(TerminalShellIntegrationMode.Detect, restored.ShellIntegration);
        Assert.Equal(TerminalBellMode.Visual, restored.BellMode);
        Assert.Equal(TerminalCompatibilityProfile.Ghostty, restored.Compatibility);
    }

    private static T RoundTrip<T>(T value)
        where T : IDurableDefinition
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");
    }
}
