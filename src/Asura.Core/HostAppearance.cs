namespace Asura.Core;

public enum HostOperatingSystem
{
    MacOS,
    Windows,
    Linux,
}

public enum LinuxDesktopEnvironment
{
    Unknown,
    Gnome,
    Kde,
}

public enum HostColorScheme
{
    Light,
    Dark,
}

public sealed record HostAppearance
{
    public HostAppearance(
        HostOperatingSystem operatingSystem,
        HostColorScheme colorScheme,
        RgbColor? accent,
        LinuxDesktopEnvironment linuxDesktop = LinuxDesktopEnvironment.Unknown,
        bool highContrast = false,
        bool reducedMotion = false,
        bool reducedTransparency = false,
        double textScale = 1,
        bool supportsAdvancedMaterials = true,
        bool supportsLiquidGlass = false)
    {
        if (!double.IsFinite(textScale) || textScale is < 0.5 or > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(textScale),
                textScale,
                "Host text scale must be between 0.5 and 4.");
        }

        if (operatingSystem != HostOperatingSystem.Linux
            && linuxDesktop != LinuxDesktopEnvironment.Unknown)
        {
            throw new ArgumentException(
                "A Linux desktop environment can only be reported by a Linux host.",
                nameof(linuxDesktop));
        }

        OperatingSystem = operatingSystem;
        ColorScheme = colorScheme;
        Accent = accent;
        LinuxDesktop = linuxDesktop;
        HighContrast = highContrast;
        ReducedMotion = reducedMotion;
        ReducedTransparency = reducedTransparency;
        TextScale = textScale;
        SupportsAdvancedMaterials = supportsAdvancedMaterials;
        SupportsLiquidGlass = supportsLiquidGlass;
    }

    public HostOperatingSystem OperatingSystem { get; }

    public HostColorScheme ColorScheme { get; }

    public RgbColor? Accent { get; }

    public LinuxDesktopEnvironment LinuxDesktop { get; }

    public bool HighContrast { get; }

    public bool ReducedMotion { get; }

    public bool ReducedTransparency { get; }

    public double TextScale { get; }

    public bool SupportsAdvancedMaterials { get; }

    public bool SupportsLiquidGlass { get; }
}
