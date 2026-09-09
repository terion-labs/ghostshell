using Asura.Core;

namespace Asura.Core.Tests;

public sealed class PlatformProfileDefaultsTests
{
    [Fact]
    public void The_older_desktop_arrives_tight_and_solid()
    {
        var defaults = PlatformProfileDefaults.For(PlatformProfile.MacOsClassic);

        Assert.NotNull(defaults);
        Assert.Equal(InterfaceDensity.Compact, defaults!.Value.Density);
        Assert.False(defaults.Value.IsTranslucent);
    }

    [Fact]
    public void The_current_one_arrives_roomy_and_glass()
    {
        var defaults = PlatformProfileDefaults.For(PlatformProfile.MacOsLiquidGlass);

        Assert.NotNull(defaults);
        Assert.Equal(InterfaceDensity.Comfortable, defaults!.Value.Density);
        Assert.True(defaults.Value.IsTranslucent);
    }

    /// <summary>
    /// Automatic follows the host, so it has nothing of its own to depart from
    /// — and a profile with no habits written down must not have habits
    /// invented for it by a comparison.
    /// </summary>
    [Theory]
    [InlineData(PlatformProfile.Automatic)]
    [InlineData(PlatformProfile.Windows11)]
    public void A_profile_without_habits_is_never_departed_from(PlatformProfile profile)
    {
        Assert.Null(PlatformProfileDefaults.For(profile));
        Assert.False(PlatformProfileDefaults.IsDepartedFrom(
            profile,
            InterfaceDensity.Compact,
            isTranslucent: true));
    }

    [Fact]
    public void Matching_what_the_profile_arrives_with_is_not_a_departure() =>
        Assert.False(PlatformProfileDefaults.IsDepartedFrom(
            PlatformProfile.MacOsLiquidGlass,
            InterfaceDensity.Comfortable,
            isTranslucent: true));

    [Fact]
    public void Changing_the_density_is_a_departure() =>
        Assert.True(PlatformProfileDefaults.IsDepartedFrom(
            PlatformProfile.MacOsLiquidGlass,
            InterfaceDensity.Compact,
            isTranslucent: true));

    [Fact]
    public void Changing_the_translucency_is_a_departure() =>
        Assert.True(PlatformProfileDefaults.IsDepartedFrom(
            PlatformProfile.MacOsClassic,
            InterfaceDensity.Compact,
            isTranslucent: true));
}
