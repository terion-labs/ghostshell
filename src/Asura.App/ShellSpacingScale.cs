using Avalonia;

namespace Asura.App;

/// <summary>
/// The interface's spacing scale: six steps derived from one host-defined grid
/// step, scaled by density and text size.
///
/// Every gap, inset, and gutter in the shell comes from here. Before this existed
/// they were literals in the markup — around 1,500 of them across a hundred
/// distinct values — which meant the density and text-size settings could reach a
/// control's own height but never the space around it. Turning density up moved
/// the controls and left the layout exactly where it was.
///
/// The steps are deliberately few. A scale with a value for every occasion is the
/// same as no scale: the point is that two things a designer would call "the same
/// gap" resolve to the same number, on every platform, at every density.
/// </summary>
internal readonly record struct ShellSpacingScale
{
    /// <summary>
    /// Multiples of the host's grid step. Halves and whole numbers only — a scale
    /// whose steps are not related by simple ratios cannot be reasoned about.
    /// </summary>
    private static readonly double[] Steps = [0.5, 1, 1.5, 2, 3, 4];

    private readonly double[] _values;

    private ShellSpacingScale(double[] values)
    {
        _values = values;
    }

    /// <summary>Hairlines: the gap inside a pill, between a glyph and its label.</summary>
    public double ExtraSmall => _values[0];

    /// <summary>The default gap between two related controls.</summary>
    public double Small => _values[1];

    /// <summary>The gap between rows in a list, and a card's inner inset.</summary>
    public double Medium => _values[2];

    /// <summary>The gap between two cards, and a page's inset.</summary>
    public double Large => _values[3];

    /// <summary>The gap between two sections of a page.</summary>
    public double ExtraLarge => _values[4];

    /// <summary>The breathing room around an empty state or a dialog's content.</summary>
    public double Huge => _values[5];

    /// <summary>
    /// Builds the scale from one grid step.
    /// </summary>
    /// <param name="unit">
    /// The host's own grid step — 8 on macOS and Windows, 6 on GNOME and KDE.
    /// Keeping it per-platform is the whole reason the scale is computed rather
    /// than written down: adapting to a new desktop is one number, not a sweep
    /// through every view.
    /// </param>
    /// <param name="scale">
    /// Density times text scale. Space has to grow with text or a larger setting
    /// just crowds the same layout.
    /// </param>
    public static ShellSpacingScale From(double unit, double scale)
    {
        if (!double.IsFinite(unit) || unit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unit),
                unit,
                "A spacing unit must be finite and greater than zero.");
        }

        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                scale,
                "A spacing scale must be finite and greater than zero.");
        }

        var values = new double[Steps.Length];
        for (var index = 0; index < Steps.Length; index++)
        {
            // Half-pixel steps. Spacing that lands between device pixels leaves a
            // seam wherever two filled surfaces meet, and the difference between
            // 11.7 and 12 is not one anybody asked for.
            values[index] = Math.Round(unit * Steps[index] * scale * 2, MidpointRounding.AwayFromZero) / 2;
        }

        return new ShellSpacingScale(values);
    }

    public Thickness Inset(double value) => new(value);

    public Thickness Inset(double horizontal, double vertical) => new(horizontal, vertical);
}
