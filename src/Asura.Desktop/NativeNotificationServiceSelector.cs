using Asura.Application;

namespace Asura.Desktop;

internal static class NativeNotificationServiceSelector
{
    public static INativeNotificationService CreateForCurrentPlatform()
    {
        try
        {
#if WINDOWS
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                return new WindowsNativeNotificationService();
            }
#endif
            if (OperatingSystem.IsMacOSVersionAtLeast(10, 14))
            {
                return new MacOsNativeNotificationService();
            }

            if (OperatingSystem.IsLinux())
            {
                return new LinuxNativeNotificationService();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Native notifications are an optional presentation effect. A
            // missing portal, app identity, or platform runtime must not keep
            // the shell from starting with its in-app unread indicators.
            Asura.Application.SecretSafeDiagnosticProjection.WriteStandardError(
                "notifications.native-service.unavailable",
                exception);
        }

        return UnavailableNativeNotificationService.Instance;
    }

    private sealed class UnavailableNativeNotificationService :
        INativeNotificationService
    {
        public static UnavailableNativeNotificationService Instance { get; } = new();

        public event EventHandler<NativeNotificationActivatedEventArgs>? Activated
        {
            add { }
            remove { }
        }

        public ValueTask ShowAsync(
            NativeNotification notification,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(notification);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
