using Asura.Application;
using Asura.Core;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class MacOsNotificationUserInfoTests
{
    [Fact]
    public void Objective_c_completion_block_can_be_copied_and_released()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var block = MacOsObjectiveCBlock.CreateError(_ => { });

        Assert.NotEqual(nint.Zero, block.Pointer);
    }

    [Fact]
    public void User_info_round_trips_notification_identity_and_full_route()
    {
        var notification = new NativeNotification(
            "notification-7",
            PanelNotificationKind.AgentCompleted,
            "Agent finished",
            "The review is ready.",
            DateTimeOffset.UtcNow,
            new NativeNotificationRoute(
                new WorkspaceInstanceId("workspace-1"),
                new TabInstanceId("tab-2"),
                new PanelInstanceId("panel-3")));

        var values = MacOsNotificationUserInfo.Create(notification);

        Assert.True(MacOsNotificationUserInfo.TryParse(
            values,
            out var notificationId,
            out var route,
            out var kind));
        Assert.Equal(notification.Id, notificationId);
        Assert.Equal(notification.Route, route);
        Assert.Equal(notification.Kind, kind);
    }

    [Fact]
    public void User_info_keeps_workspace_only_routes_compact()
    {
        var notification = new NativeNotification(
            "notification-8",
            PanelNotificationKind.FileTransferCompleted,
            "Transfer finished",
            "archive.zip",
            DateTimeOffset.UtcNow,
            new NativeNotificationRoute(
                new WorkspaceInstanceId("workspace-1")));

        var values = MacOsNotificationUserInfo.Create(notification);

        Assert.DoesNotContain(MacOsNotificationUserInfo.TabIdKey, values.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain(MacOsNotificationUserInfo.PanelIdKey, values.Keys, StringComparer.Ordinal);
        Assert.True(MacOsNotificationUserInfo.TryParse(
            values,
            out _,
            out var route,
            out var kind));
        Assert.Null(route.TabId);
        Assert.Null(route.PanelId);
        Assert.Equal(notification.Kind, kind);
    }

    [Theory]
    [InlineData(MacOsNotificationUserInfo.NotificationIdKey)]
    [InlineData(MacOsNotificationUserInfo.WorkspaceIdKey)]
    public void User_info_rejects_missing_required_identity(string missingKey)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MacOsNotificationUserInfo.NotificationIdKey] = "notification-9",
            [MacOsNotificationUserInfo.WorkspaceIdKey] = "workspace-1",
        };
        values.Remove(missingKey);

        Assert.False(MacOsNotificationUserInfo.TryParse(
            values,
            out _,
            out _,
            out _));
    }

}
