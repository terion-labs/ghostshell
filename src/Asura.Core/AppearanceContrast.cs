namespace Asura.Core;

public sealed record AppearanceContrastResult(
    double Ratio,
    double RequiredRatio,
    string Description)
{
    public bool MeetsRequirement => Ratio >= RequiredRatio;
}

/// <summary>
/// Renderer-independent contrast checks used before custom appearance values
/// reach durable settings. A renderer may still improve a color at presentation
/// time, but must not hide an inaccessible saved choice from its editor.
/// </summary>
public static class AppearanceContrast
{
    public const double NormalTextRatio = 4.5;
    public const double NonTextRatio = 3;

    public static AppearanceContrastResult TerminalForeground(TerminalPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return new(
            Ratio(palette.Foreground, palette.Background),
            NormalTextRatio,
            "Terminal foreground against terminal background");
    }

    public static AppearanceContrastResult TerminalCursor(TerminalPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return new(
            Ratio(palette.Cursor, palette.Background),
            NonTextRatio,
            "Terminal cursor against terminal background");
    }

    public static AppearanceContrastResult TerminalSelectionBackground(
        TerminalPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return new(
            Ratio(palette.SelectionBackground, palette.Background),
            NonTextRatio,
            "Terminal selection background against terminal background");
    }

    public static AppearanceContrastResult TerminalSelectionText(TerminalPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return new(
            Ratio(palette.Foreground, palette.SelectionBackground),
            NormalTextRatio,
            "Terminal foreground against terminal selection background");
    }

    public static IReadOnlyList<AppearanceContrastResult> TerminalAnsi(
        TerminalPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return [.. palette.AnsiColors.Select((color, index) => new AppearanceContrastResult(
            Ratio(color, palette.Background),
            NormalTextRatio,
            FormattableString.Invariant($"ANSI color {index} against terminal background")))];
    }

    public static AppearanceContrastResult Accent(
        RgbColor accent,
        EffectiveAppearanceMode appearance)
    {
        var background = appearance == EffectiveAppearanceMode.Light
            ? RgbColor.Parse("#F2F3F0")
            : RgbColor.Parse("#111111");
        return new(
            Ratio(accent, background),
            NonTextRatio,
            "Custom accent against the application background");
    }

    public static double Ratio(RgbColor first, RgbColor second)
    {
        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(RgbColor color) =>
        (0.2126 * Linear(color.Red))
        + (0.7152 * Linear(color.Green))
        + (0.0722 * Linear(color.Blue));

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
