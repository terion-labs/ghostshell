using System.Collections.Concurrent;
using Avalonia.Media;

namespace Asura.App.Controls;

/// <summary>
/// Resolves terminal profiles to a verified fixed-pitch typeface.
/// </summary>
/// <remarks>
/// Avalonia silently substitutes its application font when a requested family is missing.
/// That behavior is appropriate for ordinary UI, but it produces proportional glyphs on a
/// fixed terminal grid. Keep that fallback decision inside this resolver so measurement and
/// drawing can share the same verified typeface. The bundled family is the final fallback on
/// every platform; platform font catalogues never decide the terminal's default appearance.
/// </remarks>
internal static class TerminalTypefaceResolver
{
    private const char FamilyKeySeparator = '\u001f';

    private static readonly ConcurrentDictionary<ResolutionKey, Typeface> ResolvedTypefaces = [];

    public static Typeface Resolve(
        string? requestedFamily,
        FontStyle style = FontStyle.Normal,
        FontWeight weight = default,
        FontStretch stretch = default)
    {
        var normalizedWeight = weight == default ? FontWeight.Normal : weight;
        var normalizedStretch = stretch == default ? FontStretch.Normal : stretch;
        var requestedFamilies = NormalizeRequestedFamilies(requestedFamily);
        var key = new ResolutionKey(
            string.Join(FamilyKeySeparator, requestedFamilies),
            style,
            normalizedWeight,
            normalizedStretch);

        if (ResolvedTypefaces.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = TryResolve(key);
        if (resolved is null)
        {
            // Plain unit-test and design-time processes may construct controls before Avalonia
            // installs its platform font manager. Typeface resolution is lazy, so retain the
            // embedded collection identity without caching an unverified face into the later
            // desktop lifetime.
            return new Typeface(
                AsuraTerminalFontCollection.Family,
                style,
                normalizedWeight,
                normalizedStretch);
        }

        return ResolvedTypefaces.GetOrAdd(key, resolved.Value);
    }

    internal static string? SelectInstalledFamily(
        IReadOnlyList<string> requestedFamilies,
        IEnumerable<string> installedFamilies,
        Func<string, bool> isFixedPitch)
    {
        ArgumentNullException.ThrowIfNull(requestedFamilies);
        ArgumentNullException.ThrowIfNull(installedFamilies);
        ArgumentNullException.ThrowIfNull(isFixedPitch);

        var installed = installedFamilies
            .Where(family => !string.IsNullOrWhiteSpace(family))
            .Select(family => family.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(family => family, StringComparer.OrdinalIgnoreCase);
        foreach (var requested in requestedFamilies)
        {
            if (installed.TryGetValue(requested, out var exactFamily)
                && isFixedPitch(exactFamily))
            {
                return exactFamily;
            }
        }

        return null;
    }

    private static Typeface? TryResolve(ResolutionKey key)
    {
        FontManager fontManager;
        try
        {
            fontManager = FontManager.Current;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        foreach (var requestedFamily in EnumerateRequestedFamilies(key.RequestedFamilyKey))
        {
            if (string.Equals(
                    requestedFamily,
                    AsuraTerminalFontCollection.FamilyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ResolveBundled(fontManager, key);
            }

            var installedFamily = SelectInstalledFamily(
                [requestedFamily],
                fontManager.SystemFonts.Select(systemFont => systemFont.Name),
                candidate => TryResolveInstalledTypeface(
                    fontManager,
                    candidate,
                    key,
                    out _));
            if (installedFamily is not null
                && TryResolveInstalledTypeface(
                    fontManager,
                    installedFamily,
                    key,
                    out var installedTypeface))
            {
                return installedTypeface;
            }
        }

        return ResolveBundled(fontManager, key);
    }

    private static Typeface ResolveBundled(FontManager fontManager, ResolutionKey key)
    {
        var typeface = new Typeface(
            AsuraTerminalFontCollection.Family,
            key.Style,
            key.Weight,
            key.Stretch);
        if (!fontManager.TryGetGlyphTypeface(typeface, out var glyphTypeface)
            || !glyphTypeface.Metrics.IsFixedPitch
            || !string.Equals(
                glyphTypeface.FamilyName,
                AsuraTerminalFontCollection.FamilyName,
                StringComparison.Ordinal)
            || glyphTypeface.Style != key.Style
            || glyphTypeface.Weight != key.Weight
            || glyphTypeface.Stretch != key.Stretch
            || glyphTypeface.FontSimulations != FontSimulations.None)
        {
            throw new InvalidOperationException(
                "The bundled JetBrains Mono terminal face is unavailable or invalid.");
        }

        return typeface;
    }

    private static bool TryResolveInstalledTypeface(
        FontManager fontManager,
        string family,
        ResolutionKey key,
        out Typeface typeface)
    {
        typeface = new Typeface(family, key.Style, key.Weight, key.Stretch);
        try
        {
            return fontManager.TryGetGlyphTypeface(typeface, out var glyphTypeface)
                && glyphTypeface.Metrics.IsFixedPitch
                && (string.Equals(
                        glyphTypeface.FamilyName,
                        family,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        glyphTypeface.TypographicFamilyName,
                        family,
                        StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> NormalizeRequestedFamilies(string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return [];
        }

        return [.. family
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> EnumerateRequestedFamilies(string requestedFamilyKey) =>
        requestedFamilyKey.Length == 0
            ? []
            : requestedFamilyKey.Split(FamilyKeySeparator);

    private readonly record struct ResolutionKey(
        string RequestedFamilyKey,
        FontStyle Style,
        FontWeight Weight,
        FontStretch Stretch);
}
