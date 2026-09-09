using Asura.Core;

namespace Asura.App;

internal static class QuickTerminalPresentationPolicy
{
    public static bool ShouldAnimate(
        QuickTerminalSettings settings,
        HostAccessibilityPreferences hostPreferences)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostPreferences);
        return settings.AnimateSlide
            && !settings.ReduceMotion
            && !hostPreferences.ReducedMotion
            && settings.AnimationDurationMilliseconds > 0;
    }

    public static bool ShouldUseBlur(
        QuickTerminalSettings settings,
        HostAccessibilityPreferences hostPreferences)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostPreferences);
        return settings.IsTranslucent && !hostPreferences.ReducedTransparency;
    }

    public static double EffectiveOpacity(
        QuickTerminalSettings settings,
        HostAccessibilityPreferences hostPreferences)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostPreferences);
        return hostPreferences.ReducedTransparency ? 1 : settings.Opacity;
    }

    public static double HeightFraction(double height, double availableHeight)
    {
        if (!double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (!double.IsFinite(availableHeight) || availableHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableHeight));
        }

        return Math.Clamp(
            height / availableHeight,
            QuickTerminalSettings.MinimumHeightFraction,
            QuickTerminalSettings.MaximumHeightFraction);
    }
}
