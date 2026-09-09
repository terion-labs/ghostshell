namespace Asura.Core;

/// <summary>
/// The habits a platform profile arrives with.
///
/// A profile names a desktop, and desktops differ in more than their radii and
/// their type: the older Mac is tight and solid, the current one roomy and made
/// of glass. Choosing one sets these; they remain settings afterwards, because
/// a preset is a starting point and not a lock.
///
/// They live here rather than in the click handler that applies them so that
/// the same answer can be asked for twice — once to apply it, and once to say
/// whether what is stored still matches.
/// </summary>
public readonly record struct PlatformProfileDefaults(
    InterfaceDensity Density,
    bool IsTranslucent)
{
    /// <summary>
    /// Null where a profile has no opinion of its own: Automatic follows the
    /// host, and a profile the shell has no habits written down for should not
    /// have habits invented for it.
    /// </summary>
    public static PlatformProfileDefaults? For(PlatformProfile profile) => profile switch
    {
        PlatformProfile.MacOsClassic => new(InterfaceDensity.Compact, false),
        PlatformProfile.MacOsLiquidGlass => new(InterfaceDensity.Comfortable, true),
        _ => null,
    };

    /// <summary>
    /// Whether a theme has been taken away from what its profile arrived with.
    /// A profile with no habits is never departed from.
    /// </summary>
    public static bool IsDepartedFrom(
        PlatformProfile profile,
        InterfaceDensity density,
        bool isTranslucent) =>
        For(profile) is { } defaults
        && (defaults.Density != density || defaults.IsTranslucent != isTranslucent);
}
