using Asura.App;

namespace Asura.App.Tests;

/// <summary>
/// The shell's gaps were literals in the markup — around 1,500 of them across a
/// hundred distinct values — so the density and text-size settings could reach a
/// control's height but never the space around it. These pin the scale that
/// replaced them.
/// </summary>
public sealed class ShellSpacingScaleTests
{
    [Fact]
    public void The_scale_is_the_host_grid_step_and_simple_multiples_of_it()
    {
        var scale = ShellSpacingScale.From(unit: 8, scale: 1);

        Assert.Equal(4, scale.ExtraSmall);
        Assert.Equal(8, scale.Small);
        Assert.Equal(12, scale.Medium);
        Assert.Equal(16, scale.Large);
        Assert.Equal(24, scale.ExtraLarge);
        Assert.Equal(32, scale.Huge);
    }

    /// <summary>
    /// Adapting to a desktop that lays out on a different step is one number, not
    /// a sweep through every view. That is the entire reason the scale is computed.
    /// </summary>
    [Fact]
    public void A_different_host_grid_step_moves_the_whole_scale()
    {
        var scale = ShellSpacingScale.From(unit: 6, scale: 1);

        Assert.Equal(3, scale.ExtraSmall);
        Assert.Equal(6, scale.Small);
        Assert.Equal(9, scale.Medium);
        Assert.Equal(12, scale.Large);
        Assert.Equal(18, scale.ExtraLarge);
        Assert.Equal(24, scale.Huge);
    }

    /// <summary>
    /// Space has to grow with the setting. A denser interface that moves the
    /// controls and leaves the gaps alone is the defect this replaced.
    /// </summary>
    [Theory]
    [InlineData(0.78, 6)]
    [InlineData(1.0, 8)]
    [InlineData(1.22, 10)]
    public void Density_and_text_scale_carry_through_to_every_step(
        double scale,
        double expectedSmall)
    {
        Assert.Equal(expectedSmall, ShellSpacingScale.From(unit: 8, scale).Small);
    }

    /// <summary>
    /// Spacing that lands between device pixels leaves a seam wherever two filled
    /// surfaces meet.
    /// </summary>
    [Fact]
    public void Every_step_lands_on_a_half_pixel()
    {
        var scale = ShellSpacingScale.From(unit: 8, scale: 1.17);

        foreach (var value in new[]
                 {
                     scale.ExtraSmall,
                     scale.Small,
                     scale.Medium,
                     scale.Large,
                     scale.ExtraLarge,
                     scale.Huge,
                 })
        {
            Assert.Equal(value * 2, Math.Round(value * 2));
        }
    }

    [Fact]
    public void The_steps_never_decrease()
    {
        var scale = ShellSpacingScale.From(unit: 8, scale: 0.78);

        Assert.True(scale.ExtraSmall <= scale.Small);
        Assert.True(scale.Small <= scale.Medium);
        Assert.True(scale.Medium <= scale.Large);
        Assert.True(scale.Large <= scale.ExtraLarge);
        Assert.True(scale.ExtraLarge <= scale.Huge);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void A_meaningless_unit_is_rejected(double unit) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ShellSpacingScale.From(unit, scale: 1));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    public void A_meaningless_scale_is_rejected(double scale) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ShellSpacingScale.From(unit: 8, scale));
}
