using Asura.Core;

namespace Asura.Desktop;

internal static class HostAccessibilityPreferenceMapping
{
    public static HostAccessibilityPreferences FromMacOs(
        bool? reducedMotion,
        bool? reducedTransparency) => new(
            reducedMotion ?? false,
            reducedTransparency ?? false,
            textScale: 1);

    public static HostAccessibilityPreferences FromWindows(
        bool? animationsEnabled,
        bool? transparencyEnabled,
        double? textScale) => new(
            reducedMotion: animationsEnabled is false,
            reducedTransparency: transparencyEnabled is false,
            textScale: NormalizeTextScale(textScale));

    public static HostAccessibilityPreferences FromLinux(
        bool? reducedMotion,
        double? textScale) => new(
            reducedMotion: reducedMotion ?? false,
            reducedTransparency: false,
            textScale: NormalizeTextScale(textScale));

    private static double NormalizeTextScale(double? scale) => scale is { } value
        && double.IsFinite(value)
        && value is >= 0.5 and <= 4
            ? value
            : 1;
}
