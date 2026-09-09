using Asura.Application;
using Asura.Core;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class WindowsNativeNotificationActivationTests
{
    [Fact]
    public void ActivationRoundTripsStableRouteNotificationAndAction()
    {
        var notification = Notification();

        var encoded = WindowsNativeNotificationActivation.Serialize(notification);
        var parsed = WindowsNativeNotificationActivation.TryParseArguments(
            $"unrelated=value&{WindowsNativeNotificationActivation.PayloadArgument}={encoded}",
            out var activation);

        Assert.True(parsed);
        Assert.NotNull(activation);
        Assert.Equal(notification.Id, activation.NotificationId);
        Assert.Equal(notification.Route, activation.Route);
        Assert.Equal(notification.Kind, activation.Kind);
        Assert.Equal(
            WindowsNativeNotificationActivation.DefaultActionId,
            activation.ActionId);
    }

    [Fact]
    public void CustomActionRoundTripsWithoutDelimiterAmbiguity()
    {
        var notification = Notification();

        var encoded = WindowsNativeNotificationActivation.Serialize(
            notification,
            "open/details?source=notification&mode=full");
        var parsed = WindowsNativeNotificationActivation.TryParseArguments(
            $"?{WindowsNativeNotificationActivation.PayloadArgument}={encoded}",
            out var activation);

        Assert.True(parsed);
        Assert.Equal(
            "open/details?source=notification&mode=full",
            activation?.ActionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("asura")]
    [InlineData("asura=")]
    [InlineData("asura=not-base64!")]
    [InlineData("unrelated=value")]
    public void MalformedArgumentsAreRejected(string arguments)
    {
        Assert.False(WindowsNativeNotificationActivation.TryParseArguments(
            arguments,
            out var activation));
        Assert.Null(activation);
    }

    [Fact]
    public void DuplicatePayloadArgumentsAreRejected()
    {
        var encoded = WindowsNativeNotificationActivation.Serialize(Notification());

        Assert.False(WindowsNativeNotificationActivation.TryParseArguments(
            $"asura={encoded}&asura={encoded}",
            out var activation));
        Assert.Null(activation);
    }

    [Fact]
    public void OversizedRoutesAreRejectedBeforeDelivery()
    {
        var notification = Notification() with
        {
            Route = new NativeNotificationRoute(
                new WorkspaceInstanceId(new string('w', 5 * 1024))),
        };

        Assert.Throws<ArgumentException>(() =>
            WindowsNativeNotificationActivation.Serialize(notification));
    }

    [Fact]
    public void UnknownNotificationKindsAreRejectedBeforeDelivery()
    {
        var notification = Notification() with
        {
            Kind = (PanelNotificationKind)int.MaxValue,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsNativeNotificationActivation.Serialize(notification));
    }

    internal static NativeNotification Notification() => new(
        "notification/id?with=delimiters&unicode=так",
        PanelNotificationKind.AgentCompleted,
        "Build finished",
        "All targets succeeded.",
        DateTimeOffset.Parse("2026-08-18T09:15:00Z", System.Globalization.CultureInfo.InvariantCulture),
        new NativeNotificationRoute(
            new WorkspaceInstanceId("workspace/with-delimiter"),
            new TabInstanceId("tab:1"),
            new PanelInstanceId("panel?1&focus=true")));
}
