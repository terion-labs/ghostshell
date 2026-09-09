namespace Asura.Application;

public sealed class NativeNotificationActivatedEventArgs(
    string notificationId,
    NativeNotificationRoute route,
    string? actionId = null,
    PanelNotificationKind kind = PanelNotificationKind.Notification,
    string? activationToken = null) : EventArgs
{
    public string NotificationId { get; } =
        !string.IsNullOrWhiteSpace(notificationId)
            ? notificationId
            : throw new ArgumentException(
                "A native notification activation requires its notification ID.",
                nameof(notificationId));

    public NativeNotificationRoute Route { get; } =
        route ?? throw new ArgumentNullException(nameof(route));

    public string? ActionId { get; } = string.IsNullOrWhiteSpace(actionId)
        ? null
        : actionId;

    public PanelNotificationKind Kind { get; } = kind;

    /// <summary>
    /// Platform-issued token authorizing the notification click to bring an
    /// existing window to the foreground, when the native backend supplies one.
    /// </summary>
    public string? ActivationToken { get; } = string.IsNullOrWhiteSpace(activationToken)
        ? null
        : activationToken;
}
