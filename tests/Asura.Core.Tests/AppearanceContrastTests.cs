namespace Asura.Core.Tests;

public sealed class AppearanceContrastTests
{
    [Fact]
    public void Readable_terminal_palette_meets_text_and_cursor_thresholds()
    {
        Assert.True(
            AppearanceContrast.TerminalForeground(TerminalPalette.AsuraDark)
                .MeetsRequirement);
        Assert.True(
            AppearanceContrast.TerminalCursor(TerminalPalette.AsuraDark)
                .MeetsRequirement);
    }

    [Fact]
    public void Indistinguishable_custom_terminal_colors_produce_clear_failures()
    {
        var background = RgbColor.Parse("#111111");
        var palette = new TerminalPalette(
            "Low contrast",
            background,
            background,
            background,
            background,
            TerminalPalette.AsuraDark.AnsiColors);

        var foreground = AppearanceContrast.TerminalForeground(palette);
        var cursor = AppearanceContrast.TerminalCursor(palette);
        var selectionBackground =
            AppearanceContrast.TerminalSelectionBackground(palette);
        var selectionText = AppearanceContrast.TerminalSelectionText(palette);
        var ansi = AppearanceContrast.TerminalAnsi(palette);

        Assert.False(foreground.MeetsRequirement);
        Assert.Equal(1, foreground.Ratio);
        Assert.False(cursor.MeetsRequirement);
        Assert.Equal(1, cursor.Ratio);
        Assert.False(selectionBackground.MeetsRequirement);
        Assert.Equal(1, selectionBackground.Ratio);
        Assert.False(selectionText.MeetsRequirement);
        Assert.Equal(1, selectionText.Ratio);
        Assert.Contains(ansi, result => !result.MeetsRequirement);
    }

    [Theory]
    [InlineData(4.5, 4.5, true)]
    [InlineData(4.499, 4.5, false)]
    [InlineData(3, 3, true)]
    [InlineData(2.999, 3, false)]
    public void Threshold_boundary_is_inclusive(
        double ratio,
        double required,
        bool expected)
    {
        var result = new AppearanceContrastResult(ratio, required, "Boundary");

        Assert.Equal(expected, result.MeetsRequirement);
    }
}
