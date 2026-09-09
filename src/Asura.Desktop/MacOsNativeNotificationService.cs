using System.Runtime.Versioning;
using Asura.Application;

namespace Asura.Desktop;

/// <summary>
/// Delivers shell notifications through macOS UserNotifications and routes a
/// live notification click back through the platform-neutral application port.
/// </summary>
[SupportedOSPlatform("macos10.14")]
internal sealed class MacOsNativeNotificationService :
    INativeNotificationService,
    IDisposable
{
    private readonly MacOsUserNotificationCenter _notificationCenter;
    private readonly SemaphoreSlim _authorizationGate = new(1, 1);
    private bool _disposed;

    public MacOsNativeNotificationService()
        : this(new MacOsUserNotificationCenter())
    {
    }

    internal MacOsNativeNotificationService(
        MacOsUserNotificationCenter notificationCenter)
    {
        _notificationCenter = notificationCenter
            ?? throw new ArgumentNullException(nameof(notificationCenter));
        _notificationCenter.Activated += OnActivated;
    }

    public event EventHandler<NativeNotificationActivatedEventArgs>? Activated;

    public async ValueTask ShowAsync(
        NativeNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _authorizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool authorized;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            authorized = await _notificationCenter
                .RequestAuthorizationAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _authorizationGate.Release();
        }

        // Denial is a normal user decision. The shell's in-app attention mark
        // remains available, so native delivery simply becomes a no-op.
        if (!authorized)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _notificationCenter
            .AddAsync(notification, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notificationCenter.Activated -= OnActivated;
        _notificationCenter.Dispose();
    }

    private void OnActivated(
        object? sender,
        NativeNotificationActivatedEventArgs eventArgs)
    {
        _ = sender;
        var subscribers = Activated;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<NativeNotificationActivatedEventArgs> subscriber
                 in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, eventArgs);
            }
            catch (Exception exception)
            {
                ReportActivationFailure(exception);
            }
        }
    }

    private static void ReportActivationFailure(Exception exception)
    {
        try
        {
            Asura.Application.SecretSafeDiagnosticProjection.WriteStandardError(
                "notifications.macos-subscriber.failed",
                exception);
        }
        catch
        {
            // Notification activation continues even if diagnostics cannot be
            // written by this process.
        }
    }
}
