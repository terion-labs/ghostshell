namespace Asura.Core;

public enum EffectiveAppearanceMode
{
    Light,
    Dark,
}

public enum AccentSource
{
    Custom,
    Host,
    AsuraFallback,
}

public enum MaterialDisposition
{
    Enabled,
    NotRequested,
    DisabledByHighContrast,
    DisabledByReducedTransparency,
    UnsupportedByHost,
}

public sealed record EffectiveTheme(
    EffectiveAppearanceMode Appearance,
    PlatformProfile PlatformProfile,
    RgbColor Accent,
    AccentSource AccentSource,
    bool HighContrast,
    bool MotionEnabled,
    bool AdvancedMaterialsEnabled,
    double TextScale,
    InterfaceDensity Density = InterfaceDensity.Cozy,
    bool ShowTabBar = true,
    bool ShowWorkspacesPanel = true,
    TabStripPlacement TabStripPlacement = TabStripPlacement.Top,
    WorkspacePanelPlacement WorkspacePanelPlacement = WorkspacePanelPlacement.Left,
    MaterialDisposition MaterialDisposition = MaterialDisposition.Enabled,
    PlatformProfile RequestedPlatformProfile = PlatformProfile.Automatic);
