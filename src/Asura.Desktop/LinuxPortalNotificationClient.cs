using Tmds.DBus.Protocol;
using Tmds.DBus.SourceGenerator;

namespace Asura.Desktop;

internal interface ILinuxPortalNotificationClient : IDisposable
{
    ValueTask<IDisposable> WatchActionInvokedAsync(
        Action<Exception?, LinuxPortalNotificationAction> handler);

    Task AddNotificationAsync(
        string id,
        Dictionary<string, VariantValue> notification);
}

internal readonly record struct LinuxPortalNotificationAction(
    string Id,
    string Action,
    VariantValue[] Parameters);

internal sealed class LinuxPortalNotificationClient : ILinuxPortalNotificationClient
{
    private const string ServiceName = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";

#pragma warning disable CS0618 // Tmds source generator 0.0.22 still emits the legacy Connection API.
    private readonly OrgFreedesktopPortalNotificationProxy _proxy = new(
        Connection.Session,
        ServiceName,
        ObjectPath);
#pragma warning restore CS0618

    public ValueTask<IDisposable> WatchActionInvokedAsync(
        Action<Exception?, LinuxPortalNotificationAction> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _proxy.WatchActionInvokedAsync((exception, action) =>
            handler(
                exception,
                new LinuxPortalNotificationAction(
                    action.Id,
                    action.Action,
                    action.Parameter)),
            emitOnCapturedContext: false);
    }

    public Task AddNotificationAsync(
        string id,
        Dictionary<string, VariantValue> notification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(notification);
        return _proxy.AddNotificationAsync(id, notification);
    }

    public void Dispose()
    {
        // Connection.Session is shared. This client owns only its signal match.
    }
}
