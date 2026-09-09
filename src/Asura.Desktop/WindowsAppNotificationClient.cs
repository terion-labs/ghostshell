#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Asura.Desktop;

/// <summary>Owns Windows App SDK registration for the process lifetime.</summary>
internal sealed class WindowsAppNotificationClient :
    IWindowsAppNotificationClient
{
    private readonly AppNotificationManager _manager = AppNotificationManager.Default;
    private bool _registered;
    private bool _disposed;

    public event EventHandler<string>? Invoked;

    public void Register()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered)
        {
            return;
        }

        _manager.NotificationInvoked += OnNotificationInvoked;
        try
        {
            _manager.Register();
            _registered = true;
        }
        catch
        {
            _manager.NotificationInvoked -= OnNotificationInvoked;
            throw;
        }
    }

    public void Show(WindowsAppNotificationContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_registered)
        {
            throw new InvalidOperationException(
                "Windows app notifications must be registered before delivery.");
        }

        var notification = new AppNotificationBuilder()
            .AddArgument(
                WindowsNativeNotificationActivation.PayloadArgument,
                content.ActivationPayload)
            .AddText(content.Title)
            .AddText(content.Body)
            .SetTimeStamp(content.TimestampUtc)
            .BuildNotification();
        _manager.Show(notification);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_registered)
        {
            return;
        }

        _manager.NotificationInvoked -= OnNotificationInvoked;
        _manager.Unregister();
        _registered = false;
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs eventArgs) =>
        Invoked?.Invoke(this, eventArgs.Argument);
}
#endif
