using System.Runtime.InteropServices;
using Asura.Application;

namespace Asura.Desktop;

internal sealed partial class MacOsUserNotificationCenter
{
    private static nint CreateRequest(NativeNotification notification)
    {
        var content = SendObject(
            RequireClass("UNMutableNotificationContent"),
            Selector("new"));
        if (content == 0)
        {
            throw new InvalidOperationException(
                "Could not create macOS notification content.");
        }

        try
        {
            SetString(content, "setTitle:", notification.Title);
            SetString(content, "setBody:", notification.Body);
            SetString(
                content,
                "setThreadIdentifier:",
                notification.Route.WorkspaceId.Value);
            SetUserInfo(content, MacOsNotificationUserInfo.Create(notification));
            if (notification.Kind == PanelNotificationKind.Bell)
            {
                var sound = SendObject(
                    RequireClass("UNNotificationSound"),
                    Selector("defaultSound"));
                SendVoid(content, Selector("setSound:"), sound);
            }

            var identifier = CreateString(notification.Id);
            try
            {
                var request = SendObject(
                    RequireClass("UNNotificationRequest"),
                    Selector("requestWithIdentifier:content:trigger:"),
                    identifier,
                    content,
                    0);
                if (request == 0)
                {
                    throw new InvalidOperationException(
                        "Could not create a macOS notification request.");
                }

                return SendObject(request, Selector("retain"));
            }
            finally
            {
                Release(identifier);
            }
        }
        finally
        {
            Release(content);
        }
    }

    private static void SetUserInfo(
        nint content,
        IReadOnlyDictionary<string, string> values)
    {
        var dictionary = SendObject(
            RequireClass("NSMutableDictionary"),
            Selector("new"));
        if (dictionary == 0)
        {
            throw new InvalidOperationException(
                "Could not create a macOS notification payload.");
        }

        try
        {
            foreach (var (key, value) in values)
            {
                var nativeKey = CreateString(key);
                var nativeValue = CreateString(value);
                try
                {
                    SendVoid(
                        dictionary,
                        Selector("setObject:forKey:"),
                        nativeValue,
                        nativeKey);
                }
                finally
                {
                    Release(nativeValue);
                    Release(nativeKey);
                }
            }

            SendVoid(content, Selector("setUserInfo:"), dictionary);
        }
        finally
        {
            Release(dictionary);
        }
    }

    private static void SetString(nint target, string selector, string value)
    {
        var nativeValue = CreateString(value);
        try
        {
            SendVoid(target, Selector(selector), nativeValue);
        }
        finally
        {
            Release(nativeValue);
        }
    }

    private static nint CreateString(string value)
    {
        var allocated = SendObject(RequireClass("NSString"), Selector("alloc"));
        var initialized = SendObject(
            allocated,
            Selector("initWithUTF8String:"),
            value);
        return initialized != 0
            ? initialized
            : throw new InvalidOperationException(
                "Could not encode notification text for macOS.");
    }

    private static NativeNotificationActivatedEventArgs? ReadActivation(
        nint response)
    {
        var notification = SendObject(response, Selector("notification"));
        var request = SendObject(notification, Selector("request"));
        var content = SendObject(request, Selector("content"));
        var userInfo = SendObject(content, Selector("userInfo"));
        var values = ReadUserInfo(userInfo);
        if (!MacOsNotificationUserInfo.TryParse(
                values,
                out var notificationId,
                out var route,
                out var kind))
        {
            return null;
        }

        var actionId = ToManagedString(
            SendObject(response, Selector("actionIdentifier")));
        return new NativeNotificationActivatedEventArgs(
            notificationId,
            route,
            actionId,
            kind);
    }

    private void PublishActivation(
        NativeNotificationActivatedEventArgs eventArgs)
    {
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
                ReportCallbackFailure(exception);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ReadUserInfo(nint userInfo)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userInfo == 0)
        {
            return values;
        }

        foreach (var key in UserInfoKeys)
        {
            var nativeKey = CreateString(key);
            try
            {
                var nativeValue = SendObject(
                    userInfo,
                    Selector("objectForKey:"),
                    nativeKey);
                if (ToManagedString(nativeValue) is { } value)
                {
                    values[key] = value;
                }
            }
            finally
            {
                Release(nativeKey);
            }
        }

        return values;
    }

    private static string? DescribeError(nint error) => error == 0
        ? null
        : ToManagedString(SendObject(error, Selector("localizedDescription")))
          ?? "Unknown UserNotifications error";

    private static string? ToManagedString(nint value)
    {
        if (value == 0)
        {
            return null;
        }

        var utf8 = SendObject(value, Selector("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static readonly string[] UserInfoKeys =
    [
        MacOsNotificationUserInfo.NotificationIdKey,
        MacOsNotificationUserInfo.NotificationKindKey,
        MacOsNotificationUserInfo.WorkspaceIdKey,
        MacOsNotificationUserInfo.TabIdKey,
        MacOsNotificationUserInfo.PanelIdKey,
    ];
}
