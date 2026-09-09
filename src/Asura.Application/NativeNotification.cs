namespace Asura.Application;

/// <summary>Platform-neutral content sent to an operating-system notification center.</summary>
public sealed record NativeNotification(
    string Id,
    PanelNotificationKind Kind,
    string Title,
    string Body,
    DateTimeOffset TimestampUtc,
    NativeNotificationRoute Route);
