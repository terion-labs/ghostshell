using Asura.Core;

namespace Asura.Core.Tests;

/// <summary>
/// This desktop draws a window's corner at one of three radii by what kind of
/// window it is, and puts the standard buttons — and so the whole chrome band —
/// at a matching height. The two arrive together, so a density that shares a
/// window kind with its neighbour shares the frame and the header size too, and
/// stops being visible at the edge of the window at all.
/// </summary>
public sealed class DensityCornerScaleTests
{
    [Fact]
    public void Every_density_asks_for_a_window_of_its_own()
    {
        var radii = new[]
        {
            InterfaceDensity.Compact,
            InterfaceDensity.Cozy,
            InterfaceDensity.Comfortable,
        }
        .Select(DensityCornerScale.WindowRadius)
        .ToArray();

        Assert.Equal([16d, 20d, 26d], radii);
        Assert.Equal(radii.Length, radii.Distinct().Count());
    }

    /// <summary>
    /// The three window radii are the ones the platform actually draws. A
    /// number between them is not a rounder window, it is the shell's own
    /// surfaces being derived from a frame that does not exist.
    /// </summary>
    [Theory]
    [InlineData(InterfaceDensity.Compact)]
    [InlineData(InterfaceDensity.Cozy)]
    [InlineData(InterfaceDensity.Comfortable)]
    public void A_window_radius_is_one_the_platform_draws(InterfaceDensity density) =>
        Assert.Contains(DensityCornerScale.WindowRadius(density), new[] { 16d, 20d, 26d });
}
