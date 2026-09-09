using Asura.Application;

namespace Asura.Desktop;

internal enum GlobalHotkeyRuntimePlatform
{
    MacOs,
    Windows,
    Linux,
    Unsupported,
}

internal enum GlobalHotkeyBackend
{
    MacOs,
    Windows,
    LinuxX11,
    UnavailableWayland,
    UnavailableMissingX11Display,
    UnavailablePlatform,
}

internal readonly record struct LinuxDesktopSession(
    string? SessionType,
    string? X11Display,
    string? WaylandDisplay)
{
    public static LinuxDesktopSession FromEnvironment() => new(
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        Environment.GetEnvironmentVariable("DISPLAY"),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
}

internal static class GlobalHotkeyServiceSelector
{
    public static IGlobalHotkeyService CreateForCurrentPlatform()
    {
        var backend = Select(DetectCurrentPlatform(), LinuxDesktopSession.FromEnvironment());
        return backend switch
        {
            GlobalHotkeyBackend.MacOs => new MacOsGlobalHotkeyService(),
            GlobalHotkeyBackend.Windows => new WindowsGlobalHotkeyService(),
            GlobalHotkeyBackend.LinuxX11 => new LinuxX11GlobalHotkeyService(),
            GlobalHotkeyBackend.UnavailableWayland => new UnavailableGlobalHotkeyService(
                GlobalHotkeyUnavailableReason.Wayland),
            GlobalHotkeyBackend.UnavailableMissingX11Display =>
                new UnavailableGlobalHotkeyService(
                    GlobalHotkeyUnavailableReason.MissingX11Display),
            GlobalHotkeyBackend.UnavailablePlatform => new UnavailableGlobalHotkeyService(),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
        };
    }

    internal static GlobalHotkeyBackend Select(
        GlobalHotkeyRuntimePlatform platform,
        LinuxDesktopSession linuxSession) => platform switch
        {
            GlobalHotkeyRuntimePlatform.MacOs => GlobalHotkeyBackend.MacOs,
            GlobalHotkeyRuntimePlatform.Windows => GlobalHotkeyBackend.Windows,
            GlobalHotkeyRuntimePlatform.Linux => SelectLinux(linuxSession),
            GlobalHotkeyRuntimePlatform.Unsupported => GlobalHotkeyBackend.UnavailablePlatform,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        };

    private static GlobalHotkeyBackend SelectLinux(LinuxDesktopSession session)
    {
        var sessionType = session.SessionType?.Trim();
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
        {
            return GlobalHotkeyBackend.UnavailableWayland;
        }

        if (string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(session.X11Display)
                ? GlobalHotkeyBackend.UnavailableMissingX11Display
                : GlobalHotkeyBackend.LinuxX11;
        }

        if (!string.IsNullOrWhiteSpace(session.WaylandDisplay))
        {
            return GlobalHotkeyBackend.UnavailableWayland;
        }

        return string.IsNullOrWhiteSpace(session.X11Display)
            ? GlobalHotkeyBackend.UnavailableMissingX11Display
            : GlobalHotkeyBackend.LinuxX11;
    }

    private static GlobalHotkeyRuntimePlatform DetectCurrentPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return GlobalHotkeyRuntimePlatform.MacOs;
        }

        if (OperatingSystem.IsWindows())
        {
            return GlobalHotkeyRuntimePlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return GlobalHotkeyRuntimePlatform.Linux;
        }

        return GlobalHotkeyRuntimePlatform.Unsupported;
    }
}
