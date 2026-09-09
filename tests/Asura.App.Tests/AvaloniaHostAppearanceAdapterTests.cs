using Asura.App.Views;
using Asura.Core;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace Asura.App.Tests;

public sealed class AvaloniaHostAppearanceAdapterTests
{
    [Theory]
    [InlineData("ubuntu:GNOME", LinuxDesktopEnvironment.Gnome)]
    [InlineData("KDE;plasma", LinuxDesktopEnvironment.Kde)]
    [InlineData("X-Cinnamon", LinuxDesktopEnvironment.Unknown)]
    public void Linux_desktop_detection_is_case_insensitive_and_conservative(
        string desktop,
        LinuxDesktopEnvironment expected)
    {
        Assert.Equal(
            expected,
            HostPlatformCapabilities.DetectLinuxDesktop(desktop));
    }

    [Fact]
    public void Avalonia_values_map_to_the_core_host_contract()
    {
        var host = AvaloniaHostAppearanceAdapter.Map(
            PlatformThemeVariant.Light,
            (ColorContrastPreference)1,
            Color.FromRgb(0x24, 0x68, 0xAC),
            new HostPlatformCapabilities(
                HostOperatingSystem.Linux,
                LinuxDesktopEnvironment.Gnome,
                SupportsAdvancedMaterials: false,
                SupportsLiquidGlass: false));

        Assert.Equal(HostOperatingSystem.Linux, host.OperatingSystem);
        Assert.Equal(LinuxDesktopEnvironment.Gnome, host.LinuxDesktop);
        Assert.Equal(HostColorScheme.Light, host.ColorScheme);
        Assert.Equal(RgbColor.Parse("#2468AC"), host.Accent);
        Assert.True(host.HighContrast);
        Assert.False(host.SupportsAdvancedMaterials);
    }

    [Fact]
    public void Transparent_platform_accent_allows_core_bronze_fallback()
    {
        var host = AvaloniaHostAppearanceAdapter.Map(
            PlatformThemeVariant.Dark,
            ColorContrastPreference.NoPreference,
            Colors.Transparent,
            new HostPlatformCapabilities(
                HostOperatingSystem.Windows,
                LinuxDesktopEnvironment.Unknown,
                SupportsAdvancedMaterials: true,
                SupportsLiquidGlass: false));

        var effective = ThemePreference.Default.Resolve(host);

        Assert.Null(host.Accent);
        Assert.Equal(ThemePreference.BronzeFallback, effective.Accent);
        Assert.Equal(AccentSource.AsuraFallback, effective.AccentSource);
    }

    [Fact]
    public void Fresh_default_theme_maps_the_current_macos_accent_into_shared_resources()
    {
        var hostAccent = Color.FromRgb(0x18, 0x84, 0xF7);
        var host = AvaloniaHostAppearanceAdapter.Map(
            PlatformThemeVariant.Dark,
            ColorContrastPreference.NoPreference,
            hostAccent,
            new HostPlatformCapabilities(
                HostOperatingSystem.MacOS,
                LinuxDesktopEnvironment.Unknown,
                SupportsAdvancedMaterials: true,
                SupportsLiquidGlass: false));

        var effective = ThemePreference.Default.Resolve(host);
        var resources = EffectiveAppearanceResourceMapper.Map(effective);

        Assert.Equal(AccentSource.Host, effective.AccentSource);
        Assert.Equal(hostAccent, resources.Accent);
        Assert.Contains("host accent", resources.AccentStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_saved_accent_replaces_the_host_accent()
    {
        var savedAccent = RgbColor.Parse("#C04080");
        var preference = new ThemePreference(
            ThemePreference.Default.Id,
            ThemePreference.Default.Name,
            AppearanceMode.System,
            PlatformProfile.Automatic,
            AccentPreference.Custom(savedAccent));
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Light,
            RgbColor.Parse("#1884F7"));

        var effective = preference.Resolve(host);

        Assert.Equal(savedAccent, effective.Accent);
        Assert.Equal(AccentSource.Custom, effective.AccentSource);
    }

    [Fact]
    public void Quick_terminal_material_permission_comes_only_from_host_capabilities()
    {
        var allowed = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null,
            supportsAdvancedMaterials: true);
        var unsupported = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null,
            supportsAdvancedMaterials: false);
        var highContrast = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null,
            highContrast: true,
            supportsAdvancedMaterials: true);
        var reduced = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null,
            reducedTransparency: true,
            supportsAdvancedMaterials: true);

        Assert.True(App.AllowsAdvancedMaterials(allowed));
        Assert.False(App.AllowsAdvancedMaterials(unsupported));
        Assert.False(App.AllowsAdvancedMaterials(highContrast));
        Assert.False(App.AllowsAdvancedMaterials(reduced));
    }

    [Fact]
    public void Requested_liquid_glass_reports_the_classic_fallback_exactly()
    {
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null,
            supportsLiquidGlass: false);
        var resources = EffectiveAppearanceResourceMapper.Map(
            ProfilePreference(PlatformProfile.MacOsLiquidGlass).Resolve(host));

        Assert.Equal(
            "Liquid Glass unavailable; using macOS Classic",
            resources.AppearanceStatus);
    }

    [Fact]
    public void Host_accessibility_snapshot_is_merged_with_Avalonia_colors()
    {
        var accessibility = new HostAccessibilityPreferences(
            reducedMotion: true,
            reducedTransparency: true,
            textScale: 1.5);

        var host = AvaloniaHostAppearanceAdapter.Map(
            PlatformThemeVariant.Dark,
            ColorContrastPreference.NoPreference,
            Color.FromRgb(0x24, 0x68, 0xAC),
            new HostPlatformCapabilities(
                HostOperatingSystem.Windows,
                LinuxDesktopEnvironment.Unknown,
                SupportsAdvancedMaterials: true,
                SupportsLiquidGlass: false),
            accessibility);

        Assert.True(host.ReducedMotion);
        Assert.True(host.ReducedTransparency);
        Assert.Equal(1.5, host.TextScale);

        var effective = ThemePreference.Default.Resolve(host);
        Assert.False(effective.MotionEnabled);
        Assert.False(effective.AdvancedMaterialsEnabled);
        Assert.Equal(1.5, effective.TextScale);
    }

    [Fact]
    public void Platform_profile_produces_observable_metrics_and_classes()
    {
        var effective = new EffectiveTheme(
            EffectiveAppearanceMode.Light,
            PlatformProfile.Windows11,
            RgbColor.Parse("#0067C0"),
            AccentSource.Host,
            HighContrast: false,
            MotionEnabled: true,
            AdvancedMaterialsEnabled: true,
            TextScale: 1.25);

        var resources = EffectiveAppearanceResourceMapper.Map(effective);

        Assert.Equal("profile-windows11", resources.ProfileClass);
        Assert.Equal("appearance-light", resources.AppearanceClass);
        Assert.Equal(40, resources.ControlMinHeight);
        Assert.Equal(17.5, resources.BaseFontSize);
        Assert.Equal(1.25, resources.TextScale);
        Assert.Equal(31.25, resources.ScaleFontSize(25));
        Assert.Equal(17.5, resources.ScaleFontSize(10));
        Assert.Equal(FontFamily.Default, resources.FontFamily);
        Assert.True(resources.AdvancedMaterialsEnabled);
        Assert.Contains("Windows11", resources.AppearanceStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host-native profile takes the host's own interface font. Naming one did
    /// not work: "SF Pro Text" is not a family macOS resolves by name, so the
    /// stack fell through to the bundled Inter and the application looked subtly
    /// foreign on every platform it claimed to match.
    /// </summary>
    [Theory]
    [InlineData(PlatformProfile.MacOsClassic)]
    [InlineData(PlatformProfile.MacOsLiquidGlass)]
    [InlineData(PlatformProfile.Windows11)]
    [InlineData(PlatformProfile.Gnome)]
    [InlineData(PlatformProfile.Kde)]
    public void A_host_native_profile_uses_the_host_interface_font(PlatformProfile profile)
    {
        var resources = EffectiveAppearanceResourceMapper.Map(
            ProfilePreference(profile).Resolve(NativeHost));

        Assert.Equal(FontFamily.Default, resources.FontFamily);
    }

    [Theory]
    [InlineData(PlatformProfile.MacOsClassic, EffectiveAppearanceMode.Dark, 14)]
    [InlineData(PlatformProfile.MacOsLiquidGlass, EffectiveAppearanceMode.Dark, 16)]
    public void Macos_dark_profiles_use_native_window_surface_colors(
        PlatformProfile profile,
        EffectiveAppearanceMode appearance,
        double sidebarCornerRadius)
    {
        var resources = EffectiveAppearanceResourceMapper.Map(
            EffectiveThemeFor(profile, appearance));

        Assert.Equal(Color.Parse("#1B1B1B"), resources.Background);
        Assert.Equal(Color.Parse("#131313"), resources.SidebarSurface);
        Assert.Equal(Color.Parse("#343434"), resources.SidebarBorder);
        Assert.Equal(Color.Parse("#0DFFFFFF"), resources.SidebarSelectionSurface);
        Assert.Equal(Color.Parse("#242424"), resources.Surface);
        Assert.Equal(Color.Parse("#2C2C2E"), resources.RaisedSurface);
        Assert.Equal(Color.Parse("#3A3A3C"), resources.HoverSurface);
        Assert.Equal(Color.Parse("#3A3A3C"), resources.Border);
        Assert.Equal(Color.Parse("#F5F5F7"), resources.Text);
        Assert.Equal(Color.Parse("#A1A1A6"), resources.MutedText);
        Assert.Equal(new CornerRadius(sidebarCornerRadius), resources.SidebarCornerRadius);
    }

    [Theory]
    [InlineData(PlatformProfile.MacOsClassic, EffectiveAppearanceMode.Light, 14)]
    [InlineData(PlatformProfile.MacOsLiquidGlass, EffectiveAppearanceMode.Light, 16)]
    public void Macos_light_profiles_use_native_window_surface_colors(
        PlatformProfile profile,
        EffectiveAppearanceMode appearance,
        double sidebarCornerRadius)
    {
        var resources = EffectiveAppearanceResourceMapper.Map(
            EffectiveThemeFor(profile, appearance));

        Assert.Equal(Color.Parse("#FFFFFF"), resources.Background);
        Assert.Equal(Color.Parse("#FCFCFC"), resources.SidebarSurface);
        Assert.Equal(Color.Parse("#D1D1D6"), resources.SidebarBorder);
        Assert.Equal(Color.Parse("#10000000"), resources.SidebarSelectionSurface);
        Assert.Equal(Color.Parse("#F5F5F7"), resources.Surface);
        Assert.Equal(Color.Parse("#FFFFFF"), resources.RaisedSurface);
        Assert.Equal(Color.Parse("#E5E5EA"), resources.HoverSurface);
        Assert.Equal(Color.Parse("#D1D1D6"), resources.Border);
        Assert.Equal(Color.Parse("#1D1D1F"), resources.Text);
        Assert.Equal(Color.Parse("#6E6E73"), resources.MutedText);
        Assert.Equal(new CornerRadius(sidebarCornerRadius), resources.SidebarCornerRadius);
    }

    [Fact]
    public void Default_preference_on_macos_uses_the_native_window_palette()
    {
        var resources = EffectiveAppearanceResourceMapper.Map(
            ThemePreference.Default.Resolve(NativeHost));

        Assert.Equal(Color.Parse("#1B1B1B"), resources.Background);
        Assert.Equal(Color.Parse("#131313"), resources.SidebarSurface);
    }

    [Fact]
    public void Asura_profile_retains_the_product_palette()
    {
        var resources = EffectiveAppearanceResourceMapper.Map(
            EffectiveThemeFor(
                PlatformProfile.Asura,
                EffectiveAppearanceMode.Dark));

        Assert.Equal(Color.Parse("#111111"), resources.Background);
        Assert.Equal(Color.Parse("#18181B"), resources.SidebarSurface);
        Assert.Equal(Color.Parse("#18181B"), resources.Surface);
        Assert.Equal(Color.Parse("#1A1A1A"), resources.RaisedSurface);
    }

    [Fact]
    public void High_contrast_overrides_the_macos_window_palette()
    {
        var resources = EffectiveAppearanceResourceMapper.Map(new EffectiveTheme(
            EffectiveAppearanceMode.Dark,
            PlatformProfile.MacOsLiquidGlass,
            RgbColor.Parse("#FF8400"),
            AccentSource.Host,
            HighContrast: true,
            MotionEnabled: true,
            AdvancedMaterialsEnabled: true,
            TextScale: 1));

        Assert.Equal(Colors.Black, resources.Background);
        Assert.Equal(Colors.Black, resources.SidebarSurface);
        Assert.Equal(Colors.White, resources.SidebarBorder);
        Assert.Equal(Color.Parse("#202020"), resources.SidebarSelectionSurface);
        Assert.Equal(Colors.Black, resources.Surface);
        Assert.Equal(Colors.White, resources.Border);
    }

    [Theory]
    [InlineData(EffectiveAppearanceMode.Dark)]
    [InlineData(EffectiveAppearanceMode.Light)]
    public void High_contrast_accent_remains_legible_on_the_selected_sidebar_surface(
        EffectiveAppearanceMode appearance)
    {
        var resources = EffectiveAppearanceResourceMapper.Map(new EffectiveTheme(
            appearance,
            PlatformProfile.MacOsLiquidGlass,
            RgbColor.Parse("#767676"),
            AccentSource.Custom,
            HighContrast: true,
            MotionEnabled: true,
            AdvancedMaterialsEnabled: true,
            TextScale: 1));

        Assert.True(
            EffectiveAppearanceResourceMapper.ContrastRatio(
                resources.Accent,
                resources.SidebarSelectionSurface) >= 4.5);
    }

    /// <summary>
    /// Asura's own profile is deliberately not the host's: it ships a known
    /// typeface so the product looks the same everywhere it runs.
    /// </summary>
    [Fact]
    public void The_products_own_profile_names_its_typeface()
    {
        var resources = EffectiveAppearanceResourceMapper.Map(
            ProfilePreference(PlatformProfile.Asura).Resolve(NativeHost));

        Assert.NotEqual(FontFamily.Default, resources.FontFamily);
        Assert.Contains("Inter", resources.FontFamily.Name, StringComparison.Ordinal);
    }

    private static HostAppearance NativeHost { get; } = new(
        HostOperatingSystem.MacOS,
        HostColorScheme.Dark,
        accent: null);

    private static ThemePreference ProfilePreference(PlatformProfile profile) => new(
        ThemePreference.Default.Id,
        ThemePreference.Default.Name,
        AppearanceMode.Dark,
        profile,
        AccentPreference.AsuraBronze);

    private static EffectiveTheme EffectiveThemeFor(
        PlatformProfile profile,
        EffectiveAppearanceMode appearance) => new(
        appearance,
        profile,
        RgbColor.Parse("#FF8400"),
        AccentSource.Host,
        HighContrast: false,
        MotionEnabled: true,
        AdvancedMaterialsEnabled: true,
        TextScale: 1);

    [Fact]
    public void Application_text_scale_override_reaches_Avalonia_metrics()
    {
        var preference = new ThemePreference(
            new ThemePreferenceId("large-ui"),
            "Large UI",
            AppearanceMode.Dark,
            PlatformProfile.Asura,
            AccentPreference.AsuraBronze,
            textScaleOverride: 2);
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null,
            textScale: 1.25);

        var resources = EffectiveAppearanceResourceMapper.Map(preference.Resolve(host));

        Assert.Equal(2, resources.TextScale);
        Assert.Equal(26, resources.BaseFontSize);
        Assert.Equal(50, resources.ScaleFontSize(25));
    }

    [Theory]
    [InlineData(1, 10, 13)]
    [InlineData(2, 20, 26)]
    public void Pill_typography_stays_below_body_text_and_tracks_text_scale(
        double textScale,
        double expectedPillFontSize,
        double expectedBodyFontSize)
    {
        var effective = new EffectiveTheme(
            EffectiveAppearanceMode.Dark,
            PlatformProfile.Asura,
            RgbColor.Parse("#FF8400"),
            AccentSource.Host,
            HighContrast: false,
            MotionEnabled: true,
            AdvancedMaterialsEnabled: true,
            TextScale: textScale);

        var resources = EffectiveAppearanceResourceMapper.Map(effective);

        Assert.Equal(expectedPillFontSize, resources.PillFontSize);
        Assert.Equal(expectedBodyFontSize, resources.BaseFontSize);
        Assert.True(resources.PillFontSize < resources.BaseFontSize);
    }

    [Fact]
    public void Appearance_text_scale_options_cover_high_scale_and_preserve_imported_values()
    {
        Assert.Contains(
            MainWindow.AppearanceTextScaleOptions,
            option => option.Scale is null && string.Equals(option.DisplayName, "Follow host", StringComparison.Ordinal));
        Assert.Contains(
            MainWindow.AppearanceTextScaleOptions,
            option => option.Scale == 2 && string.Equals(option.DisplayName, "200%", StringComparison.Ordinal));
        Assert.Contains(
            MainWindow.AppearanceTextScaleOptions,
            option => option.Scale == 2.5 && string.Equals(option.DisplayName, "250%", StringComparison.Ordinal));

        var imported = MainWindow.ResolveApplicationTextScaleOption(1.3);

        Assert.Equal("130%", imported.DisplayName);
        Assert.Equal(1.3, imported.Scale);
    }

    private static EffectiveTheme Bronze(
        EffectiveAppearanceMode appearance,
        bool highContrast = false) =>
        new(
            appearance,
            PlatformProfile.Asura,
            RgbColor.Parse("#FF8400"),
            AccentSource.Host,
            HighContrast: highContrast,
            MotionEnabled: true,
            AdvancedMaterialsEnabled: false,
            TextScale: 1);

    /// <summary>
    /// What sits on a filled accent control. Measured contrast puts near-black on
    /// the bronze accent, which is what the reference frames draw — but on a dark
    /// interface that reads as a warning label rather than the primary action.
    /// </summary>
    [Fact]
    public void A_dark_theme_puts_white_on_a_filled_accent_control()
    {
        Assert.Equal(
            Colors.White,
            EffectiveAppearanceResourceMapper.Map(
                Bronze(EffectiveAppearanceMode.Dark)).AccentForeground);
    }

    [Fact]
    public void A_light_theme_keeps_the_measured_choice()
    {
        Assert.Equal(
            Colors.Black,
            EffectiveAppearanceResourceMapper.Map(
                Bronze(EffectiveAppearanceMode.Light)).AccentForeground);
    }

    /// <summary>
    /// White on the bronze accent is about 2.5:1, under the 4.5:1 normal text is
    /// meant to clear. High contrast is the one setting where that is not a
    /// trade anyone should be making on the user's behalf.
    /// </summary>
    [Fact]
    public void High_contrast_keeps_the_measured_choice_even_in_the_dark()
    {
        var resources = EffectiveAppearanceResourceMapper.Map(
            Bronze(EffectiveAppearanceMode.Dark, highContrast: true));

        Assert.True(
            EffectiveAppearanceResourceMapper.ContrastRatio(
                resources.Accent,
                resources.AccentForeground) >= 4.5);
    }

    [Fact]
    public void A_button_is_inset_more_generously_than_an_input()
    {
        var resources = EffectiveAppearanceResourceMapper.Map(
            Bronze(EffectiveAppearanceMode.Dark));

        Assert.True(resources.ButtonPadding.Left > resources.ControlPadding.Left);
        Assert.True(resources.ButtonPadding.Top > resources.ControlPadding.Top);
    }

    [Fact]
    public void High_contrast_mapping_keeps_accent_legible_against_the_surface()
    {
        var effective = new EffectiveTheme(
            EffectiveAppearanceMode.Dark,
            PlatformProfile.Asura,
            RgbColor.Parse("#111111"),
            AccentSource.Custom,
            HighContrast: true,
            MotionEnabled: false,
            AdvancedMaterialsEnabled: false,
            TextScale: 1);

        var resources = EffectiveAppearanceResourceMapper.Map(effective);

        Assert.True(resources.HighContrast);
        Assert.False(resources.MotionEnabled);
        Assert.Equal(Colors.Black, resources.Background);
        Assert.True(
            EffectiveAppearanceResourceMapper.ContrastRatio(
                resources.Accent,
                resources.Background) >= 4.5);
        Assert.Contains("contrast adjusted", resources.AccentStatus, StringComparison.Ordinal);
    }
}
