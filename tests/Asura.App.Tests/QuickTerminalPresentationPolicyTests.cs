using Asura.Core;

namespace Asura.App.Tests;

public sealed class QuickTerminalPresentationPolicyTests
{
    [Theory]
    [InlineData(520, 1_000, 0.52)]
    [InlineData(100, 1_000, QuickTerminalSettings.MinimumHeightFraction)]
    [InlineData(950, 1_000, QuickTerminalSettings.MaximumHeightFraction)]
    public void Resized_height_is_stored_as_a_bounded_monitor_fraction(
        double height,
        double availableHeight,
        double expected)
    {
        Assert.Equal(
            expected,
            QuickTerminalPresentationPolicy.HeightFraction(height, availableHeight),
            precision: 6);
    }

    [Fact]
    public void Default_presentation_uses_the_stored_motion_blur_and_opacity()
    {
        var settings = QuickTerminalSettings.Default;

        Assert.True(QuickTerminalPresentationPolicy.ShouldAnimate(
            settings,
            HostAccessibilityPreferences.Default));
        Assert.True(QuickTerminalPresentationPolicy.ShouldUseBlur(
            settings,
            HostAccessibilityPreferences.Default));
        Assert.Equal(
            settings.Opacity,
            QuickTerminalPresentationPolicy.EffectiveOpacity(
                settings,
                HostAccessibilityPreferences.Default));
    }

    [Fact]
    public void Host_accessibility_preferences_override_visual_effects()
    {
        var host = new HostAccessibilityPreferences(
            reducedMotion: true,
            reducedTransparency: true,
            textScale: 1);

        Assert.False(QuickTerminalPresentationPolicy.ShouldAnimate(
            QuickTerminalSettings.Default,
            host));
        Assert.False(QuickTerminalPresentationPolicy.ShouldUseBlur(
            QuickTerminalSettings.Default,
            host));
        Assert.Equal(
            1,
            QuickTerminalPresentationPolicy.EffectiveOpacity(
                QuickTerminalSettings.Default,
                host));
    }

    [Fact]
    public void Stored_reduce_motion_remains_more_restrictive_than_the_host()
    {
        var defaults = QuickTerminalSettings.Default;
        var settings = new QuickTerminalSettings(
            defaults.Id,
            defaults.Name,
            defaults.Hotkey,
            defaults.MonitorPolicy,
            defaults.HeightFraction,
            defaults.Opacity,
            defaults.AnimateSlide,
            defaults.AnimationDurationMilliseconds,
            reduceMotion: true,
            restoreLastSession: defaults.RestoreLastSession,
            hideOnFocusLoss: defaults.HideOnFocusLoss,
            isTranslucent: defaults.IsTranslucent);

        Assert.False(QuickTerminalPresentationPolicy.ShouldAnimate(
            settings,
            HostAccessibilityPreferences.Default));
    }

    [Fact]
    public void Zero_tint_opacity_keeps_the_independent_platform_blur()
    {
        var defaults = QuickTerminalSettings.Default;
        var settings = new QuickTerminalSettings(
            defaults.Id,
            defaults.Name,
            defaults.Hotkey,
            defaults.MonitorPolicy,
            defaults.HeightFraction,
            opacity: 0,
            defaults.AnimateSlide,
            defaults.AnimationDurationMilliseconds,
            defaults.ReduceMotion,
            defaults.RestoreLastSession,
            defaults.HideOnFocusLoss,
            isTranslucent: true,
            defaults.RestoreOnStart);

        Assert.True(QuickTerminalPresentationPolicy.ShouldUseBlur(
            settings,
            HostAccessibilityPreferences.Default));
        Assert.Equal(
            0,
            QuickTerminalPresentationPolicy.EffectiveOpacity(
                settings,
                HostAccessibilityPreferences.Default));
    }

}
