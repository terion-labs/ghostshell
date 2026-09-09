using System.Text.Json.Serialization;

namespace Asura.Core;

public enum AppearanceMode
{
    System,
    Light,
    Dark,
}

public enum PlatformProfile
{
    Automatic,
    MacOsClassic,
    MacOsLiquidGlass,
    Windows11,
    Gnome,
    Kde,
    Asura,
    Custom,
}

/// <summary>Interface density, which scales control padding and heights.</summary>
public enum InterfaceDensity
{
    Compact,
    Cozy,
    Comfortable,
}

/// <summary>Which edge the tab strip sits on.</summary>
public enum TabStripPlacement
{
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>Which edge the workspaces rail docks to.</summary>
public enum WorkspacePanelPlacement
{
    Left,
    Right,
}

public sealed record ThemePreference : IDurableDefinition
{
    /// <summary>
    /// Version 2 adds the window-chrome settings. Database migration 20 upgrades
    /// version-1 documents before the strict durable-definition codec reads them.
    /// </summary>
    // The later translucency change did not require schema 3. Migration 20 also
    // normalizes the older schema-2 blur and corner properties before strict
    // deserialization.
    public const int CurrentSchemaVersion = 2;
    /// <summary>Whether the shell sits on a translucent base surface at all.</summary>
    public const bool DefaultIsTranslucent = true;

    /// <summary>
    /// Whether the panels standing on the base are glass too, rather than
    /// near-solid slabs on it. This remains off as the durable fallback for
    /// older documents and platforms whose first-run preference does not opt
    /// into glass panels.
    /// </summary>
    public const bool DefaultHasGlassPanels = false;

    /// <summary>
    /// Whether the shell paints its own opacity over the material, rather than
    /// letting the material's own translucency stand. On by default: the shell
    /// has a colour it means to be, and the platform's glass is lighter than
    /// it.
    /// </summary>
    public const bool DefaultOverridesBackdropOpacity = true;

    /// <summary>
    /// How solid the shell's base surface is, as a percentage. The blur is
    /// only half of glass; the other half is how much of the blurred desktop
    /// is allowed through. Near 100 the surface reads as a painted frame
    /// around the panels rather than as a material, which is the difference
    /// between a dark gutter and a window you can see into.
    /// </summary>
    public const int MinimumBackdropOpacityPercent = 40;

    public const int MaximumBackdropOpacityPercent = 100;

    // Seventy-eight is where the dark shell sits against the platform's own
    // glass rather than in front of it.
    public const int DefaultBackdropOpacityPercent = 78;

    public static RgbColor BronzeFallback { get; } = RgbColor.Parse("#B8793A");

    public static ThemePreference Default { get; } = new(
        new ThemePreferenceId("builtin.theme.automatic"),
        "Automatic",
        AppearanceMode.System,
        PlatformProfile.Automatic,
        AccentPreference.FollowHost);

    public static ThemePreference DefaultFor(HostOperatingSystem operatingSystem) =>
        operatingSystem == HostOperatingSystem.MacOS
            ? new(
                Default.Id,
                Default.Name,
                Default.Appearance,
                Default.PlatformProfile,
                Default.Accent,
                density: InterfaceDensity.Cozy,
                isTranslucent: true,
                backdropOpacityPercent: DefaultBackdropOpacityPercent,
                hasGlassPanels: true,
                overridesBackdropOpacity: false)
            : Default;

    public ThemePreference(
        ThemePreferenceId id,
        string name,
        AppearanceMode appearance,
        PlatformProfile platformProfile,
        AccentPreference accent,
        double? textScaleOverride = null,
        InterfaceDensity density = InterfaceDensity.Cozy,
        bool showTabBar = true,
        bool showWorkspacesPanel = true,
        TabStripPlacement tabStripPlacement = TabStripPlacement.Top,
        WorkspacePanelPlacement workspacePanelPlacement = WorkspacePanelPlacement.Left,
        bool isTranslucent = DefaultIsTranslucent,
        int backdropOpacityPercent = DefaultBackdropOpacityPercent,
        bool hasGlassPanels = DefaultHasGlassPanels,
        bool overridesBackdropOpacity = DefaultOverridesBackdropOpacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(accent);
        if (textScaleOverride is { } scale
            && (!double.IsFinite(scale) || scale is < 0.5 or > 4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(textScaleOverride),
                textScaleOverride,
                "Application text scale must be between 0.5 and 4.");
        }

        if (backdropOpacityPercent is < MinimumBackdropOpacityPercent
            or > MaximumBackdropOpacityPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(backdropOpacityPercent),
                backdropOpacityPercent,
                $"Backdrop opacity must be between {MinimumBackdropOpacityPercent} and "
                + $"{MaximumBackdropOpacityPercent}.");
        }

        if (!Enum.IsDefined(density))
        {
            throw new ArgumentOutOfRangeException(nameof(density), density, "Unknown density.");
        }

        if (!Enum.IsDefined(tabStripPlacement))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tabStripPlacement),
                tabStripPlacement,
                "Unknown tab placement.");
        }

        if (!Enum.IsDefined(workspacePanelPlacement))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workspacePanelPlacement),
                workspacePanelPlacement,
                "Unknown workspace-panel placement.");
        }

        Id = id;
        Name = name;
        Appearance = appearance;
        PlatformProfile = platformProfile;
        Accent = accent;
        TextScaleOverride = textScaleOverride;
        Density = density;
        ShowTabBar = showTabBar;
        ShowWorkspacesPanel = showWorkspacesPanel;
        TabStripPlacement = tabStripPlacement;
        WorkspacePanelPlacement = workspacePanelPlacement;
        IsTranslucent = isTranslucent;
        HasGlassPanels = hasGlassPanels;
        OverridesBackdropOpacity = overridesBackdropOpacity;
        BackdropOpacityPercent = backdropOpacityPercent;
    }

    public static DefinitionKind Kind => DefinitionKind.Theme;

    public ThemePreferenceId Id { get; }

    public int SchemaVersion => CurrentSchemaVersion;

    public string Name { get; }

    public AppearanceMode Appearance { get; }

    public PlatformProfile PlatformProfile { get; }

    public AccentPreference Accent { get; }

    public double? TextScaleOverride { get; }

    /// <summary>Null follows the platform profile's own radius.</summary>
    public InterfaceDensity Density { get; }

    public bool ShowTabBar { get; }

    public bool ShowWorkspacesPanel { get; }

    public TabStripPlacement TabStripPlacement { get; }

    public WorkspacePanelPlacement WorkspacePanelPlacement { get; }

    /// <summary>
    /// Whether the shell sits on a translucent base surface.
    ///
    /// This was a blur radius, which only ever meant pixels because macOS was
    /// blurred by an explicit radius underneath. The shell now hands the
    /// platform its own material, and every platform's is a capability rather
    /// than a number — so the only part of that setting which still decided
    /// anything was whether it was zero.
    /// </summary>
    public bool IsTranslucent { get; }

    /// <summary>How solid the base surface is, as a percentage.</summary>
    public int BackdropOpacityPercent { get; }

    /// <summary>
    /// Whether the panels are the same glass as the base rather than nearly
    /// solid over it.
    /// </summary>
    public bool HasGlassPanels { get; }

    /// <summary>
    /// Whether the stored opacity is painted over the material, or the
    /// material's own translucency is left to stand.
    /// </summary>
    public bool OverridesBackdropOpacity { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public EffectiveTheme Resolve(HostAppearance host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var appearance = Appearance switch
        {
            AppearanceMode.Light => EffectiveAppearanceMode.Light,
            AppearanceMode.Dark => EffectiveAppearanceMode.Dark,
            AppearanceMode.System when host.ColorScheme == HostColorScheme.Light => EffectiveAppearanceMode.Light,
            _ => EffectiveAppearanceMode.Dark,
        };

        var platformProfile = ResolvePlatformProfile(host);
        var (accent, source) = ResolveAccent(host);
        var materialDisposition = !IsTranslucent
            ? MaterialDisposition.NotRequested
            : host.HighContrast
                ? MaterialDisposition.DisabledByHighContrast
                : host.ReducedTransparency
                    ? MaterialDisposition.DisabledByReducedTransparency
                    : !host.SupportsAdvancedMaterials
                        ? MaterialDisposition.UnsupportedByHost
                        : MaterialDisposition.Enabled;

        // Transparency effects follow the host's own reduced-transparency and
        // high-contrast preferences, so an accessibility setting is never
        // overridden by a stored profile.
        return new EffectiveTheme(
            appearance,
            platformProfile,
            accent,
            source,
            host.HighContrast,
            !host.ReducedMotion,
            materialDisposition == MaterialDisposition.Enabled,
            TextScaleOverride ?? host.TextScale,
            Density,
            ShowTabBar,
            ShowWorkspacesPanel,
            TabStripPlacement,
            WorkspacePanelPlacement,
            materialDisposition,
            PlatformProfile);
    }

    private PlatformProfile ResolvePlatformProfile(HostAppearance host)
    {
        if (PlatformProfile == PlatformProfile.MacOsLiquidGlass && !host.SupportsLiquidGlass)
        {
            return PlatformProfile.MacOsClassic;
        }

        if (PlatformProfile != PlatformProfile.Automatic)
        {
            return PlatformProfile;
        }

        return host.OperatingSystem switch
        {
            HostOperatingSystem.MacOS when host.SupportsLiquidGlass => PlatformProfile.MacOsLiquidGlass,
            HostOperatingSystem.MacOS => PlatformProfile.MacOsClassic,
            HostOperatingSystem.Windows => PlatformProfile.Windows11,
            HostOperatingSystem.Linux when host.LinuxDesktop == LinuxDesktopEnvironment.Gnome => PlatformProfile.Gnome,
            HostOperatingSystem.Linux when host.LinuxDesktop == LinuxDesktopEnvironment.Kde => PlatformProfile.Kde,
            _ => PlatformProfile.Asura,
        };
    }

    private (RgbColor Color, AccentSource Source) ResolveAccent(HostAppearance host) => Accent.Kind switch
    {
        AccentPreferenceKind.Custom => (Accent.CustomColor!.Value, AccentSource.Custom),
        AccentPreferenceKind.FollowHost when host.Accent is { } hostAccent => (hostAccent, AccentSource.Host),
        _ => (BronzeFallback, AccentSource.AsuraFallback),
    };
}
