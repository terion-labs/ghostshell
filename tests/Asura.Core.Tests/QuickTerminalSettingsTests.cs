using System.Text.Json;

namespace Asura.Core.Tests;

public sealed class QuickTerminalSettingsTests
{
    [Fact]
    public void Defaults_match_the_functional_drop_down_profile()
    {
        var settings = QuickTerminalSettings.Default;

        Assert.Equal(DefinitionKind.QuickTerminalSettings, settings.Key.Kind);
        Assert.Equal(new KeyStroke("GRAVE", KeyModifiers.Meta), settings.Hotkey);
        Assert.Equal(QuickTerminalMonitorPolicy.MainWindow, settings.MonitorPolicy);
        Assert.Equal(0.55, settings.HeightFraction);
        Assert.Equal(0.82, settings.Opacity);
        Assert.True(settings.IsTranslucent);
        Assert.True(settings.AnimateSlide);
        Assert.True(settings.RestoreLastSession);
        Assert.True(settings.RestoreOnStart);
        Assert.True(settings.HideOnFocusLoss);
    }

    [Theory]
    [InlineData(0.24, 0.82, 180)]
    [InlineData(0.91, 0.82, 180)]
    [InlineData(0.55, -0.01, 180)]
    [InlineData(0.55, 1.01, 180)]
    [InlineData(0.55, 0.82, -1)]
    [InlineData(0.55, 0.82, 1001)]
    public void Invalid_visual_ranges_are_rejected(
        double height,
        double opacity,
        int duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            height,
            opacity,
            duration));
    }

    [Theory]
    [InlineData(0.00)]
    [InlineData(0.20)]
    [InlineData(0.40)]
    public void Low_opacity_values_are_valid(double opacity)
    {
        var settings = Create(
            height: 0.55,
            opacity,
            duration: 180);

        Assert.Equal(opacity, settings.Opacity);
    }

    [Fact]
    public void Durable_payload_round_trips_every_behavior_choice()
    {
        var original = new QuickTerminalSettings(
            new QuickTerminalSettingsId("custom"),
            "Custom Quick Terminal",
            new KeyStroke("K", KeyModifiers.Control | KeyModifiers.Shift),
            QuickTerminalMonitorPolicy.ActiveWindow,
            0.4,
            0.2,
            animateSlide: false,
            animationDurationMilliseconds: 0,
            reduceMotion: true,
            restoreLastSession: false,
            hideOnFocusLoss: false,
            restoreOnStart: false);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<QuickTerminalSettings>(json);

        Assert.Equal(original, restored);
        Assert.False(restored!.RestoreOnStart);
    }

    private static QuickTerminalSettings Create(
        double height,
        double opacity,
        int duration) => new(
            new QuickTerminalSettingsId("test"),
            "Test",
            new KeyStroke("GRAVE", KeyModifiers.Meta),
            QuickTerminalMonitorPolicy.MainWindow,
            height,
            opacity,
            animateSlide: true,
            animationDurationMilliseconds: duration,
            reduceMotion: false,
            restoreLastSession: true,
            hideOnFocusLoss: true);
}
