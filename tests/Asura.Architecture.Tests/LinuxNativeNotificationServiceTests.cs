using Asura.Application;
using Asura.Core;
using Asura.Desktop;
using Tmds.DBus.Protocol;

namespace Asura.Architecture.Tests;

public sealed class LinuxNativeNotificationServiceTests
{
    [Fact]
    public void PortalPayloadCarriesContentActionAndSerializedRoute()
    {
        var notification = Notification();

        var payload = LinuxNativeNotificationService.CreatePortalNotification(notification);

        Assert.Equal("Build finished", payload["title"].GetString());
        Assert.Equal("All targets succeeded.", payload["body"].GetString());
        Assert.Equal("normal", payload["priority"].GetString());
        Assert.Equal(
            LinuxNativeNotificationService.DefaultAction,
            payload["default-action"].GetString());
        var routePayload = payload["default-action-target"]
            .GetVariantValue()
            .GetString();
        Assert.True(LinuxNativeNotificationRoute.TryDeserialize(
            routePayload,
            out var route,
            out var kind));
        Assert.Equal(notification.Route, route);
        Assert.Equal(notification.Kind, kind);
    }

    [Fact]
    public async Task ShowAddsNotificationAfterActionSubscriptionIsReady()
    {
        var client = new FakeLinuxPortalNotificationClient();
        using var service = new LinuxNativeNotificationService(client);

        await service.ShowAsync(Notification(), CancellationToken.None);

        Assert.True(client.Watched);
        var added = Assert.Single(client.Added);
        Assert.Equal("notification-1", added.Id);
        Assert.Equal("Build finished", added.Payload["title"].GetString());
    }

    [Fact]
    public async Task RunningProcessActionRaisesSerializedRouteAndNotificationId()
    {
        var client = new FakeLinuxPortalNotificationClient();
        using var service = new LinuxNativeNotificationService(client);
        await service.Initialization;
        var notification = Notification();
        NativeNotificationActivatedEventArgs? activated = null;
        service.Activated += (_, eventArgs) => activated = eventArgs;

        client.Emit(new LinuxPortalNotificationAction(
            notification.Id,
            LinuxNativeNotificationService.DefaultAction,
            [
                VariantValue.Variant(
                    LinuxNativeNotificationRoute.Serialize(
                        notification.Route,
                        notification.Kind)),
                VariantValue.Variant(new Dict<string, VariantValue>
                {
                    ["activation-token"] = "wayland-token",
                }),
            ]));

        Assert.NotNull(activated);
        Assert.Equal(notification.Id, activated.NotificationId);
        Assert.Equal(notification.Route, activated.Route);
        Assert.Equal(notification.Kind, activated.Kind);
        Assert.Equal(LinuxNativeNotificationService.DefaultAction, activated.ActionId);
        Assert.Equal("wayland-token", activated.ActivationToken);
    }

    [Fact]
    public async Task ThrowingActivationSubscriberDoesNotEscapeOrBlockLaterSubscribers()
    {
        var client = new FakeLinuxPortalNotificationClient();
        using var service = new LinuxNativeNotificationService(client);
        await service.Initialization;
        var laterSubscriberCalls = 0;
        service.Activated += (_, _) => throw new InvalidOperationException("subscriber failed");
        service.Activated += (_, _) => laterSubscriberCalls++;

        client.Emit(new LinuxPortalNotificationAction(
            Notification().Id,
            LinuxNativeNotificationService.DefaultAction,
            [VariantValue.Variant(
                LinuxNativeNotificationRoute.Serialize(
                    Notification().Route,
                    Notification().Kind))]));

        Assert.Equal(1, laterSubscriberCalls);
    }

    [Fact]
    public async Task InvalidActionsAreIgnoredAndDisposalReleasesTheMatch()
    {
        var client = new FakeLinuxPortalNotificationClient();
        var service = new LinuxNativeNotificationService(client);
        await service.Initialization;
        var activationCount = 0;
        service.Activated += (_, _) => activationCount++;

        client.Emit(new LinuxPortalNotificationAction(
            "notification-1",
            LinuxNativeNotificationService.DefaultAction,
            ["not-json"]));
        service.Dispose();
        client.Emit(new LinuxPortalNotificationAction(
            "notification-1",
            LinuxNativeNotificationService.DefaultAction,
            [LinuxNativeNotificationRoute.Serialize(Notification().Route)]));

        Assert.Equal(0, activationCount);
        Assert.True(client.SubscriptionDisposed);
        Assert.True(client.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await service.ShowAsync(Notification(), CancellationToken.None));
    }

    [Fact]
    public async Task SubscriptionCompletingAfterDisposalIsReleased()
    {
        var client = new FakeLinuxPortalNotificationClient(delaySubscription: true);
        var service = new LinuxNativeNotificationService(client);
        var initialization = service.Initialization;

        service.Dispose();
        client.CompleteSubscription();
        await initialization;

        Assert.True(client.SubscriptionDisposed);
    }

    private static NativeNotification Notification() => new(
        "notification-1",
        PanelNotificationKind.AgentCompleted,
        "Build finished",
        "All targets succeeded.",
        DateTimeOffset.Parse("2026-08-18T09:15:00Z", System.Globalization.CultureInfo.InvariantCulture),
        new NativeNotificationRoute(
            new WorkspaceInstanceId("workspace/with-delimiter"),
            new TabInstanceId("tab:1"),
            new PanelInstanceId("panel?1")));

    private sealed class FakeLinuxPortalNotificationClient :
        ILinuxPortalNotificationClient
    {
        private readonly TaskCompletionSource<IDisposable>? _pendingSubscription;
        private Action<Exception?, LinuxPortalNotificationAction>? _handler;

        public FakeLinuxPortalNotificationClient(bool delaySubscription = false)
        {
            if (delaySubscription)
            {
                _pendingSubscription = new TaskCompletionSource<IDisposable>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public List<(string Id, Dictionary<string, VariantValue> Payload)> Added { get; } = [];

        public bool Disposed { get; private set; }

        public bool SubscriptionDisposed { get; private set; }

        public bool Watched { get; private set; }

        public ValueTask<IDisposable> WatchActionInvokedAsync(
            Action<Exception?, LinuxPortalNotificationAction> handler)
        {
            _handler = handler;
            Watched = true;
            return _pendingSubscription is null
                ? ValueTask.FromResult<IDisposable>(Subscription())
                : new ValueTask<IDisposable>(_pendingSubscription.Task);
        }

        public Task AddNotificationAsync(
            string id,
            Dictionary<string, VariantValue> notification)
        {
            Added.Add((id, notification));
            return Task.CompletedTask;
        }

        public void CompleteSubscription() =>
            _pendingSubscription?.TrySetResult(Subscription());

        public void Emit(LinuxPortalNotificationAction action) =>
            _handler?.Invoke(null, action);

        public void Dispose() => Disposed = true;

        private IDisposable Subscription() => new CallbackDisposable(
            () => SubscriptionDisposed = true);
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
