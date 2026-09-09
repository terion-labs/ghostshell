using Asura.Application;
using Asura.Core;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Asura.App;

internal sealed class AvaloniaHostAppearanceAdapter
{
    private readonly IPlatformSettings _platformSettings;
    private readonly HostPlatformCapabilities _capabilities;
    private readonly IHostAccessibilityPreferencesSource? _accessibilityPreferences;

    public AvaloniaHostAppearanceAdapter(IPlatformSettings platformSettings)
        : this(platformSettings, HostPlatformCapabilities.Detect())
    {
    }

    public AvaloniaHostAppearanceAdapter(
        IPlatformSettings platformSettings,
        IHostAccessibilityPreferencesSource accessibilityPreferences)
        : this(
            platformSettings,
            HostPlatformCapabilities.Detect(),
            accessibilityPreferences)
    {
    }

    internal AvaloniaHostAppearanceAdapter(
        IPlatformSettings platformSettings,
        HostPlatformCapabilities capabilities,
        IHostAccessibilityPreferencesSource? accessibilityPreferences = null)
    {
        _platformSettings = platformSettings
            ?? throw new ArgumentNullException(nameof(platformSettings));
        _capabilities = capabilities;
        _accessibilityPreferences = accessibilityPreferences;
    }

    public HostAppearance GetCurrent()
    {
        var colors = _platformSettings.GetColorValues();
        return Map(
            colors.ThemeVariant,
            colors.ContrastPreference,
            colors.AccentColor1,
            _capabilities,
            _accessibilityPreferences?.Current ?? HostAccessibilityPreferences.Default);
    }

    internal static HostAppearance Map(
        PlatformThemeVariant themeVariant,
        ColorContrastPreference contrastPreference,
        Color accent,
        HostPlatformCapabilities capabilities) => Map(
            themeVariant,
            contrastPreference,
            accent,
            capabilities,
            HostAccessibilityPreferences.Default);

    internal static HostAppearance Map(
        PlatformThemeVariant themeVariant,
        ColorContrastPreference contrastPreference,
        Color accent,
        HostPlatformCapabilities capabilities,
        HostAccessibilityPreferences accessibilityPreferences)
    {
        ArgumentNullException.ThrowIfNull(accessibilityPreferences);
        var colorScheme = themeVariant == PlatformThemeVariant.Light
            ? HostColorScheme.Light
            : HostColorScheme.Dark;
        RgbColor? hostAccent = accent.A == 0
            ? null
            : new RgbColor(accent.R, accent.G, accent.B);

        return new HostAppearance(
            capabilities.OperatingSystem,
            colorScheme,
            hostAccent,
            capabilities.LinuxDesktop,
            highContrast: contrastPreference != ColorContrastPreference.NoPreference,
            reducedMotion: accessibilityPreferences.ReducedMotion,
            reducedTransparency: accessibilityPreferences.ReducedTransparency,
            textScale: accessibilityPreferences.TextScale,
            supportsAdvancedMaterials: capabilities.SupportsAdvancedMaterials,
            supportsLiquidGlass: capabilities.SupportsLiquidGlass);
    }
}

internal sealed record HostPlatformCapabilities(
    HostOperatingSystem OperatingSystem,
    LinuxDesktopEnvironment LinuxDesktop,
    bool SupportsAdvancedMaterials,
    bool SupportsLiquidGlass)
{
    public static HostPlatformCapabilities Detect()
    {
        if (System.OperatingSystem.IsMacOS())
        {
            return new HostPlatformCapabilities(
                HostOperatingSystem.MacOS,
                LinuxDesktopEnvironment.Unknown,
                SupportsAdvancedMaterials: true,
                SupportsLiquidGlass: System.OperatingSystem.IsMacOSVersionAtLeast(26));
        }

        if (System.OperatingSystem.IsWindows())
        {
            return new HostPlatformCapabilities(
                HostOperatingSystem.Windows,
                LinuxDesktopEnvironment.Unknown,
                SupportsAdvancedMaterials: true,
                SupportsLiquidGlass: false);
        }

        return new HostPlatformCapabilities(
            HostOperatingSystem.Linux,
            DetectLinuxDesktop(
                Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
                Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"),
                Environment.GetEnvironmentVariable("DESKTOP_SESSION")),
            SupportsAdvancedMaterials: false,
            SupportsLiquidGlass: false);
    }

    internal static LinuxDesktopEnvironment DetectLinuxDesktop(params string?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var description = string.Join(':', values.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (description.Contains("KDE", StringComparison.OrdinalIgnoreCase)
            || description.Contains("PLASMA", StringComparison.OrdinalIgnoreCase))
        {
            return LinuxDesktopEnvironment.Kde;
        }

        if (description.Contains("GNOME", StringComparison.OrdinalIgnoreCase))
        {
            return LinuxDesktopEnvironment.Gnome;
        }

        return LinuxDesktopEnvironment.Unknown;
    }
}

internal sealed record EffectiveAppearanceResources(
    ThemeVariant ThemeVariant,
    string ProfileClass,
    string AppearanceClass,
    Color Background,
    Color SidebarSurface,
    Color SidebarBorder,
    Color SidebarSelectionSurface,
    Color Surface,
    Color RaisedSurface,
    Color HoverSurface,
    Color Border,
    Color ControlSurface,
    Color ControlBorder,
    Color ControlHoverSurface,
    Color Text,
    Color MutedText,
    Color Accent,
    Color AccentForeground,
    Color AccentSoft,
    Color Danger,
    Color DangerForeground,
    Color DangerSoft,
    Color DangerBorder,
    Color Success,
    Color SuccessSoft,
    Color SuccessBorder,
    Color Warning,
    Color WarningSoft,
    Color WarningBorder,
    Color NoticeBorder,
    FontFamily FontFamily,
    double BaseFontSize,
    double TextScale,
    double PillFontSize,
    double ControlMinHeight,
    CornerRadius ControlCornerRadius,
    Thickness ControlPadding,
    Thickness ButtonPadding,
    CornerRadius CardCornerRadius,
    CornerRadius SidebarCornerRadius,
    CornerRadius PillCornerRadius,
    CornerRadius InnerCornerRadius,
    ShellSpacingScale Spacing,
    string AppearanceStatus,
    string AccentStatus,
    string MaterialStatus,
    bool HighContrast,
    bool MotionEnabled,
    bool AdvancedMaterialsEnabled)
{
    /// <summary>
    /// How much of the blurred backdrop the window is allowed to leave visible at
    /// full blur strength. Blur is a backdrop effect: it is only ever visible
    /// where the window does not paint over it, so the strength has to act on the
    /// window's own fill. The floor keeps the interface legible instead of
    /// letting text sit directly on the desktop.
    /// </summary>
    internal double ScaleFontSize(double baseFontSize)
    {
        if (!double.IsFinite(baseFontSize) || baseFontSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseFontSize),
                baseFontSize,
                "A base font size must be finite and greater than zero.");
        }

        // Small labels still use the platform's normal readable UI size. The
        // numeric tokens express hierarchy, not permission to render application
        // chrome below the host's standard body text.
        return Math.Max(BaseFontSize, baseFontSize * TextScale);
    }
}

internal static class EffectiveAppearanceResourceMapper
{
    private const double PillBaseFontSize = 10;

    public static EffectiveAppearanceResources Map(EffectiveTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var isLight = theme.Appearance == EffectiveAppearanceMode.Light;
        var isMacOsProfile = theme.PlatformProfile is
            PlatformProfile.MacOsClassic or PlatformProfile.MacOsLiquidGlass;
        // Finder separates its content plane from its sidebar material. Keep that
        // distinction semantic: using the sidebar tone as the general surface
        // would also recolor cards, panel headers, and status bars.
        // The Control* trio is what push buttons and other clickable chrome are
        // made of. macOS raises its controls well above the surrounding surface
        // — a dark-mode push button is a distinctly lighter gray with a
        // translucent hairline — while the product's own profile keeps controls
        // nearly flush with their card. Separating the tokens lets each host
        // profile answer differently without recoloring cards and panels.
        var palette = (isMacOsProfile, isLight) switch
        {
            (true, false) => (
                // What Finder's window is, so a shell sitting beside one on the
                // same desktop reads as belonging to it rather than as almost
                // matching it.
                Background: "#1B1B1B",
                // And what its sidebar is, which is not simply the window a
                // shade down: the platform sets the two separately and a
                // sidebar guessed from the window reads as almost matching.
                Sidebar: "#131313",
                SidebarBorder: "#343434",
                SidebarSelection: "#0DFFFFFF",
                Surface: "#242424",
                Raised: "#2C2C2E",
                Hover: "#3A3A3C",
                Border: "#3A3A3C",
                ControlSurface: "#48484A",
                ControlBorder: "#14FFFFFF",
                ControlHover: "#545457",
                Text: "#F5F5F7",
                Muted: "#A1A1A6"),
            (true, true) => (
                Background: "#FFFFFF",
                Sidebar: "#FCFCFC",
                SidebarBorder: "#D1D1D6",
                SidebarSelection: "#10000000",
                Surface: "#F5F5F7",
                Raised: "#FFFFFF",
                Hover: "#E5E5EA",
                Border: "#D1D1D6",
                ControlSurface: "#FFFFFF",
                ControlBorder: "#D1D1D6",
                ControlHover: "#F2F2F4",
                Text: "#1D1D1F",
                Muted: "#6E6E73"),
            (false, false) => (
                Background: "#111111",
                Sidebar: "#18181B",
                SidebarBorder: "#38383C",
                SidebarSelection: "#0DFFFFFF",
                Surface: "#18181B",
                Raised: "#1A1A1A",
                Hover: "#2E2E2E",
                Border: "#38383C",
                ControlSurface: "#1A1A1A",
                ControlBorder: "#38383C",
                ControlHover: "#2E2E2E",
                Text: "#FFFFFF",
                Muted: "#B8B9B6"),
            _ => (
                Background: "#F2F3F0",
                Sidebar: "#E7E8E5",
                SidebarBorder: "#B8BAB6",
                SidebarSelection: "#10000000",
                Surface: "#E7E8E5",
                Raised: "#FFFFFF",
                Hover: "#D7D8D5",
                Border: "#B8BAB6",
                ControlSurface: "#FFFFFF",
                ControlBorder: "#B8BAB6",
                ControlHover: "#D7D8D5",
                Text: "#111111",
                Muted: "#5C5E5A"),
        };
        var background = Parse(palette.Background);
        var sidebar = Parse(palette.Sidebar);
        var sidebarBorder = Parse(palette.SidebarBorder);
        var sidebarSelection = Parse(palette.SidebarSelection);
        var surface = Parse(palette.Surface);
        var raised = Parse(palette.Raised);
        var hover = Parse(palette.Hover);
        var border = Parse(palette.Border);
        var controlSurface = Parse(palette.ControlSurface);
        var controlBorder = Parse(palette.ControlBorder);
        var controlHover = Parse(palette.ControlHover);
        var text = Parse(palette.Text);
        var muted = Parse(palette.Muted);

        if (theme.HighContrast)
        {
            background = Parse(isLight ? "#FFFFFF" : "#000000");
            sidebar = Parse(isLight ? "#FFFFFF" : "#000000");
            sidebarBorder = Parse(isLight ? "#000000" : "#FFFFFF");
            sidebarSelection = Parse(isLight ? "#E6E6E6" : "#202020");
            surface = Parse(isLight ? "#FFFFFF" : "#000000");
            raised = Parse(isLight ? "#FFFFFF" : "#080808");
            hover = Parse(isLight ? "#E6E6E6" : "#202020");
            border = Parse(isLight ? "#000000" : "#FFFFFF");
            controlSurface = raised;
            controlBorder = border;
            controlHover = hover;
            text = Parse(isLight ? "#000000" : "#FFFFFF");
            muted = Parse(isLight ? "#262626" : "#E6E6E6");
        }

        var requestedAccent = ToColor(theme.Accent);
        var accent = EnsureContrast(
            requestedAccent,
            background,
            theme.HighContrast ? 4.5 : 3.0,
            isLight ? Colors.Black : Colors.White);
        if (theme.HighContrast)
        {
            // Selected sidebar labels use the accent directly. The high-contrast
            // selection plane is intentionally distinct from the window, so an
            // accent that only clears the window can still fail on that row.
            accent = EnsureContrast(
                accent,
                sidebarSelection,
                4.5,
                text);
        }

        // What sits on a filled accent control.
        //
        // Picking whichever of black and white has more contrast is the accessible
        // answer and gives near-black on the bronze accent — which is what the
        // reference frames draw. On a dark interface it reads as a warning label
        // rather than as the primary action, so dark themes take white and light
        // themes keep the measured choice.
        //
        // White on the default accent is about 2.5:1, below the 4.5:1 that normal
        // text is meant to clear. High contrast is the one place that is not
        // acceptable, so it keeps the measured choice whatever the theme.
        var measuredAccentForeground = ContrastRatio(accent, Colors.Black)
            >= ContrastRatio(accent, Colors.White)
            ? Colors.Black
            : Colors.White;
        var accentForeground = !isLight && !theme.HighContrast
            ? Colors.White
            : measuredAccentForeground;
        var accentSoft = Blend(accent, background, theme.HighContrast ? 0.24 : 0.18);
        var metrics = PlatformMetrics.For(theme.PlatformProfile);
        var densityScale = DensityScale(theme.Density);
        // The profile says what each role is worth and the style scales the set,
        // so the distances between roles survive being adjusted. One number
        // standing for every role was the thing that could not be right: a
        // control and a window's base surface do not want the same corner, and
        // the old override gave them the same one plus a fixed nudge.
        var cornerScale = DensityCornerScale.For(theme.Density);
        var controlRadius = metrics.ControlCornerRadius * cornerScale;
        var cardRadius = metrics.CardCornerRadius * cornerScale;
        var sidebarRadius = metrics.SidebarCornerRadius * cornerScale;
        var source = theme.AccentSource switch
        {
            AccentSource.Custom => "custom accent",
            AccentSource.Host => "host accent",
            _ => "Asura bronze fallback",
        };

        var danger = Parse(isLight ? "#B42318" : "#FF7B72");
        var dangerForeground = ContrastRatio(danger, Colors.Black)
            >= ContrastRatio(danger, Colors.White)
            ? Colors.Black
            : Colors.White;

        return new EffectiveAppearanceResources(
            isLight ? ThemeVariant.Light : ThemeVariant.Dark,
            ProfileClass(theme.PlatformProfile),
            isLight ? "appearance-light" : "appearance-dark",
            background,
            sidebar,
            sidebarBorder,
            sidebarSelection,
            surface,
            raised,
            hover,
            border,
            controlSurface,
            controlBorder,
            controlHover,
            text,
            muted,
            accent,
            accentForeground,
            accentSoft,
            danger,
            dangerForeground,
            Parse(isLight ? "#FDE8E5" : "#3A1715"),
            Parse(isLight ? "#A73528" : "#A84C45"),
            Parse(isLight ? "#147A3F" : "#77D797"),
            Parse(isLight ? "#E2F5E8" : "#173222"),
            Parse(isLight ? "#41935E" : "#2E6743"),
            Parse(isLight ? "#8A4B08" : "#E1A45F"),
            Parse(isLight ? "#FFF0D6" : "#32241A"),
            Parse(isLight ? "#B46B20" : "#75502F"),
            Blend(accent, border, 0.55),
            ResolveFontFamily(metrics),
            metrics.BaseFontSize * theme.TextScale,
            theme.TextScale,
            PillBaseFontSize * theme.TextScale,
            metrics.ControlMinHeight * theme.TextScale * densityScale,
            new CornerRadius(controlRadius),
            new Thickness(
                metrics.HorizontalPadding * theme.TextScale * densityScale,
                metrics.VerticalPadding * theme.TextScale * densityScale),
            new Thickness(
                metrics.ButtonHorizontalPadding * theme.TextScale * densityScale,
                metrics.ButtonVerticalPadding * theme.TextScale * densityScale),
            new CornerRadius(cardRadius),
            new CornerRadius(sidebarRadius),
            // A pill is fully round whatever the radius setting says; a shape
            // nested inside a card is rounder than nothing and tighter than its
            // parent, so it never reads as a second card.
            new CornerRadius(999),
            new CornerRadius(Math.Max(2, controlRadius - 2)),
            ShellSpacingScale.From(metrics.SpaceUnit, theme.TextScale * densityScale),
            AppearanceStatus(theme, isLight),
            $"Accent: {source}" +
            (accent == requestedAccent ? string.Empty : " · contrast adjusted"),
            MaterialStatus(theme.MaterialDisposition),
            theme.HighContrast,
            theme.MotionEnabled,
            theme.AdvancedMaterialsEnabled);
    }

    private static string AppearanceStatus(EffectiveTheme theme, bool isLight)
    {
        if (theme.RequestedPlatformProfile == PlatformProfile.MacOsLiquidGlass
            && theme.PlatformProfile == PlatformProfile.MacOsClassic)
        {
            return "Liquid Glass unavailable; using macOS Classic";
        }

        return $"Effective: {theme.PlatformProfile} · {(isLight ? "Light" : "Dark")}" +
            (theme.HighContrast ? " · High contrast" : string.Empty);
    }

    private static string MaterialStatus(MaterialDisposition disposition) => disposition switch
    {
        MaterialDisposition.Enabled => "Window material: active",
        MaterialDisposition.NotRequested => "Window material: off",
        MaterialDisposition.DisabledByHighContrast =>
            "Window material: opaque fallback (high contrast)",
        MaterialDisposition.DisabledByReducedTransparency =>
            "Window material: opaque fallback (reduced transparency)",
        _ => "Window material: opaque fallback (unsupported by this host)",
    };

    /// <summary>
    /// Density scales the padding and minimum height the platform profile asks
    /// for, so a denser setting stays proportional to each host's own metrics
    /// instead of replacing them with fixed numbers.
    /// </summary>
    private static double DensityScale(InterfaceDensity density) => density switch
    {
        InterfaceDensity.Compact => 0.78,
        InterfaceDensity.Comfortable => 1.22,
        _ => 1,
    };

    internal static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static Color EnsureContrast(
        Color candidate,
        Color background,
        double minimumRatio,
        Color target)
    {
        if (ContrastRatio(candidate, background) >= minimumRatio)
        {
            return candidate;
        }

        for (var step = 1; step <= 20; step++)
        {
            var adjusted = Blend(target, candidate, step / 20d);
            if (ContrastRatio(adjusted, background) >= minimumRatio)
            {
                return adjusted;
            }
        }

        return target;
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * Linearize(color.R / 255d)
        + 0.7152 * Linearize(color.G / 255d)
        + 0.0722 * Linearize(color.B / 255d);

    private static double Linearize(double component) => component <= 0.04045
        ? component / 12.92
        : Math.Pow((component + 0.055) / 1.055, 2.4);

    private static Color Blend(Color foreground, Color background, double foregroundAmount)
    {
        var amount = Math.Clamp(foregroundAmount, 0, 1);
        return Color.FromRgb(
            Blend(foreground.R, background.R, amount),
            Blend(foreground.G, background.G, amount),
            Blend(foreground.B, background.B, amount));
    }

    private static byte Blend(byte foreground, byte background, double foregroundAmount) =>
        checked((byte)Math.Round(
            foreground * foregroundAmount + background * (1 - foregroundAmount)));

    private static string ProfileClass(PlatformProfile profile) => profile switch
    {
        PlatformProfile.MacOsClassic => "profile-macos-classic",
        PlatformProfile.MacOsLiquidGlass => "profile-macos-liquid-glass",
        PlatformProfile.Windows11 => "profile-windows11",
        PlatformProfile.Gnome => "profile-gnome",
        PlatformProfile.Kde => "profile-kde",
        PlatformProfile.Custom => "profile-custom",
        _ => "profile-asura",
    };

    private static Color Parse(string value) => Color.Parse(value);

    private static Color ToColor(RgbColor color) => Color.FromRgb(
        color.Red,
        color.Green,
        color.Blue);

    /// <summary>
    /// The interface typeface for a platform profile.
    ///
    /// A host-native profile takes the host's own UI font rather than naming one.
    /// Naming it did not work: "SF Pro Text" is not a family macOS resolves by
    /// name, so the stack fell through to the bundled Inter and the application
    /// looked subtly foreign on every platform it claimed to match.
    /// </summary>
    private static FontFamily ResolveFontFamily(PlatformMetrics metrics) =>
        metrics.UsesSystemFont
            ? FontFamily.Default
            : new FontFamily(metrics.FontFamily);

    private sealed record PlatformMetrics(
        string FontFamily,
        bool UsesSystemFont,
        double BaseFontSize,
        double ControlMinHeight,
        double ControlCornerRadius,
        double HorizontalPadding,
        double VerticalPadding,
        double CardCornerRadius,
        double SidebarCornerRadius,
        double SpaceUnit,
        double ButtonHorizontalPadding,
        double ButtonVerticalPadding)
    {
        public static PlatformMetrics For(PlatformProfile profile) => profile switch
        {
            // The trailing number on each profile is the desktop's own grid step,
            // which the whole spacing scale is derived from: Aqua and Fluent lay
            // out on 8, Adwaita and Breeze on 6.
            PlatformProfile.MacOsClassic => new(
                "SF Pro Text, Inter, sans-serif",
                UsesSystemFont: true,
                13,
                28,
                7,
                10,
                6,
                10,
                14,
                8,
                16,
                10),
            PlatformProfile.MacOsLiquidGlass => new(
                "SF Pro Text, Inter, sans-serif",
                UsesSystemFont: true,
                13,
                30,
                10,
                11,
                6,
                13,
                16,
                8,
                16,
                10),
            PlatformProfile.Windows11 => new(
                "Segoe UI Variable, Segoe UI, Inter, sans-serif",
                UsesSystemFont: true,
                14,
                32,
                6,
                11,
                6,
                8,
                8,
                8,
                16,
                9),
            PlatformProfile.Gnome => new(
                "Cantarell, Inter, sans-serif",
                UsesSystemFont: true,
                11,
                34,
                9,
                12,
                7,
                12,
                12,
                6,
                16,
                10),
            PlatformProfile.Kde => new(
                "Noto Sans, Inter, sans-serif",
                UsesSystemFont: true,
                10,
                30,
                4,
                10,
                6,
                6,
                6,
                6,
                14,
                8),
            // Asura's own profile is deliberately not the host's: it ships a
            // known typeface so the product looks the same everywhere.
            _ => new(
                "Inter, Segoe UI, sans-serif",
                UsesSystemFont: false,
                13,
                30,
                7,
                10,
                6,
                9,
                9,
                8,
                16,
                10),
        };
    }
}
