namespace Asura.Application;

/// <summary>
/// Delivers shell notifications through the host operating system. Activation
/// is routed back through stable runtime identities rather than view objects.
/// </summary>
public interface INativeNotificationService
{
    event EventHandler<NativeNotificationActivatedEventArgs>? Activated;

    ValueTask ShowAsync(
        NativeNotification notification,
        CancellationToken cancellationToken);
}
