using Asura.Application;
using Asura.Core;

namespace Asura.Desktop;

/// <summary>
/// Serializes stable shell routes into the string-only payload carried by a
/// macOS notification. Keeping view objects out of <c>userInfo</c> lets a click
/// be resolved even after the notification outlives the view that produced it.
/// </summary>
internal static class MacOsNotificationUserInfo
{
    internal const string NotificationIdKey = "asura.notification.id";
    internal const string NotificationKindKey = "asura.notification.kind";
    internal const string WorkspaceIdKey = "asura.workspace.id";
    internal const string TabIdKey = "asura.tab.id";
    internal const string PanelIdKey = "asura.panel.id";

    public static IReadOnlyDictionary<string, string> Create(
        NativeNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NotificationIdKey] = notification.Id,
            [NotificationKindKey] = notification.Kind.ToString(),
            [WorkspaceIdKey] = notification.Route.WorkspaceId.Value,
        };
        if (notification.Route.TabId is { } tabId)
        {
            values[TabIdKey] = tabId.Value;
        }

        if (notification.Route.PanelId is { } panelId)
        {
            values[PanelIdKey] = panelId.Value;
        }

        return values;
    }

    public static bool TryParse(
        IReadOnlyDictionary<string, string> values,
        out string notificationId,
        out NativeNotificationRoute route,
        out PanelNotificationKind kind)
    {
        ArgumentNullException.ThrowIfNull(values);

        notificationId = string.Empty;
        route = null!;
        kind = PanelNotificationKind.Notification;
        if (!TryGetRequired(values, NotificationIdKey, out notificationId)
            || !TryGetRequired(values, WorkspaceIdKey, out var workspaceId))
        {
            return false;
        }

        try
        {
            route = new NativeNotificationRoute(
                new WorkspaceInstanceId(workspaceId),
                GetOptional(values, TabIdKey) is { } tabId
                    ? new TabInstanceId(tabId)
                    : null,
                GetOptional(values, PanelIdKey) is { } panelId
                    ? new PanelInstanceId(panelId)
                    : null);
            if (GetOptional(values, NotificationKindKey) is { } kindValue
                && (!Enum.TryParse(kindValue, ignoreCase: false, out kind)
                    || !Enum.IsDefined(kind)))
            {
                kind = PanelNotificationKind.Notification;
            }

            return true;
        }
        catch (ArgumentException)
        {
            notificationId = string.Empty;
            route = null!;
            kind = PanelNotificationKind.Notification;
            return false;
        }
    }

    private static bool TryGetRequired(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        if (values.TryGetValue(key, out var candidate)
            && !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? GetOptional(
        IReadOnlyDictionary<string, string> values,
        string key) =>
        values.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
}
