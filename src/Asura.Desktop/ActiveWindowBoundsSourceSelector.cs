using Asura.App;

namespace Asura.Desktop;

internal static class ActiveWindowBoundsSourceSelector
{
    public static IActiveWindowBoundsSource CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacOsActiveWindowBoundsSource();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsActiveWindowBoundsSource();
        }

        if (OperatingSystem.IsLinux() && IsX11Session(LinuxDesktopSession.FromEnvironment()))
        {
            return new LinuxX11ActiveWindowBoundsSource();
        }

        return new UnavailableActiveWindowBoundsSource();
    }

    internal static bool IsX11Session(LinuxDesktopSession session)
    {
        var sessionType = session.SessionType?.Trim();
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(session.WaylandDisplay))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(session.X11Display);
    }
}

internal sealed class UnavailableActiveWindowBoundsSource : IActiveWindowBoundsSource
{
    public Avalonia.PixelRect? TryGetBounds() => null;
}
