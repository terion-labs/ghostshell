using Asura.Application;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class WindowsNativeNotificationServiceTests
{
    [Fact]
    public async Task ConstructionRegistersAndShowBuildsRoutedContent()
    {
        var client = new FakeWindowsAppNotificationClient();
        using var service = new WindowsNativeNotificationService(client);
        var notification = WindowsNativeNotificationActivationTests.Notification();

        await service.ShowAsync(notification, CancellationToken.None);

        Assert.Equal(1, client.RegisterCount);
        var content = Assert.Single(client.Shown);
        Assert.Equal(notification.Title, content.Title);
        Assert.Equal(notification.Body, content.Body);
        Assert.Equal(notification.TimestampUtc, content.TimestampUtc);
        Assert.True(WindowsNativeNotificationActivation.TryParseArguments(
            $"{WindowsNativeNotificationActivation.PayloadArgument}={content.ActivationPayload}",
            out var activation));
        Assert.Equal(notification.Id, activation?.NotificationId);
        Assert.Equal(notification.Route, activation?.Route);
        Assert.Equal(notification.Kind, activation?.Kind);
    }

    [Fact]
    public void RunningProcessInvocationRaisesCompleteActivation()
    {
        var client = new FakeWindowsAppNotificationClient();
        using var service = new WindowsNativeNotificationService(client);
        var notification = WindowsNativeNotificationActivationTests.Notification();
        NativeNotificationActivatedEventArgs? activation = null;
        service.Activated += (_, eventArgs) => activation = eventArgs;
        var payload = WindowsNativeNotificationActivation.Serialize(
            notification,
            "open-details");

        client.Emit($"asura={payload}");

        Assert.NotNull(activation);
        Assert.Equal(notification.Id, activation.NotificationId);
        Assert.Equal(notification.Route, activation.Route);
        Assert.Equal("open-details", activation.ActionId);
        Assert.Equal(notification.Kind, activation.Kind);
    }

    [Fact]
    public void ThrowingActivationSubscriberDoesNotEscapeOrPreventLaterSubscriber()
    {
        var client = new FakeWindowsAppNotificationClient();
        using var service = new WindowsNativeNotificationService(client);
        var notification = WindowsNativeNotificationActivationTests.Notification();
        var laterSubscriberCalls = 0;
        service.Activated += (_, _) =>
            throw new InvalidOperationException("subscriber failed");
        service.Activated += (_, _) => laterSubscriberCalls++;
        var payload = WindowsNativeNotificationActivation.Serialize(notification);

        var exception = Record.Exception(() => client.Emit($"asura={payload}"));

        Assert.Null(exception);
        Assert.Equal(1, laterSubscriberCalls);
    }

    [Fact]
    public async Task CancellationAndDisposalPreventDelivery()
    {
        var client = new FakeWindowsAppNotificationClient();
        var service = new WindowsNativeNotificationService(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await service.ShowAsync(
                WindowsNativeNotificationActivationTests.Notification(),
                cancellation.Token));
        service.Dispose();
        client.Emit("asura=ignored");

        Assert.Empty(client.Shown);
        Assert.True(client.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await service.ShowAsync(
                WindowsNativeNotificationActivationTests.Notification(),
                CancellationToken.None));
    }

    [Fact]
    public void FailedRegistrationReleasesClient()
    {
        var client = new FakeWindowsAppNotificationClient
        {
            RegisterException = new InvalidOperationException("registration failed"),
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new WindowsNativeNotificationService(client));

        Assert.Equal("registration failed", exception.Message);
        Assert.True(client.Disposed);
    }

    private sealed class FakeWindowsAppNotificationClient :
        IWindowsAppNotificationClient
    {
        public event EventHandler<string>? Invoked;

        public bool Disposed { get; private set; }

        public int RegisterCount { get; private set; }

        public Exception? RegisterException { get; init; }

        public List<WindowsAppNotificationContent> Shown { get; } = [];

        public void Register()
        {
            RegisterCount++;
            if (RegisterException is not null)
            {
                throw RegisterException;
            }
        }

        public void Show(WindowsAppNotificationContent content) => Shown.Add(content);

        public void Emit(string arguments) => Invoked?.Invoke(this, arguments);

        public void Dispose() => Disposed = true;
    }
}
