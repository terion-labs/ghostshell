using Asura.Application;

namespace Asura.Desktop;

internal static class HostAccessibilityPreferencesSourceSelector
{
    public static IHostAccessibilityPreferencesSource CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsMacOSVersionAtLeast(10, 12))
        {
            return new MacOsHostAccessibilityPreferencesSource();
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
#if WINDOWS
            return new WindowsHostAccessibilityPreferencesSource();
#else
            return new NullHostAccessibilityPreferencesSource();
#endif
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxHostAccessibilityPreferencesSource();
        }

        return new NullHostAccessibilityPreferencesSource();
    }
}
