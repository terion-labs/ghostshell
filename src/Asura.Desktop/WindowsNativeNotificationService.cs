using Asura.Application;

namespace Asura.Desktop;

/// <summary>
/// Publishes Windows app notifications and maps clicks received by the running
/// process back to stable Asura runtime identities.
/// </summary>
public sealed class WindowsNativeNotificationService :
    INativeNotificationService,
    IDisposable
{
    private readonly object _lifetimeGate = new();
    private readonly IWindowsAppNotificationClient _client;
    private bool _disposed;

    public WindowsNativeNotificationService()
        : this(CreateClient())
    {
    }

    internal WindowsNativeNotificationService(IWindowsAppNotificationClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.Invoked += OnInvoked;
        try
        {
            _client.Register();
        }
        catch
        {
            _client.Invoked -= OnInvoked;
            _client.Dispose();
            throw;
        }
    }

    public event EventHandler<NativeNotificationActivatedEventArgs>? Activated;

    public ValueTask ShowAsync(
        NativeNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Id);
        cancellationToken.ThrowIfCancellationRequested();

        var content = new WindowsAppNotificationContent(
            notification.Title,
            notification.Body,
            notification.TimestampUtc,
            WindowsNativeNotificationActivation.Serialize(notification));
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _client.Show(content);

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        IWindowsAppNotificationClient client;
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _client.Invoked -= OnInvoked;
            client = _client;
        }

        client.Dispose();
    }

    private void OnInvoked(object? sender, string arguments)
    {
        if (!WindowsNativeNotificationActivation.TryParseArguments(
                arguments,
                out var activation)
            || activation is null)
        {
            return;
        }

        EventHandler<NativeNotificationActivatedEventArgs>? handler;
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            handler = Activated;
        }

        if (handler is null)
        {
            return;
        }

        foreach (EventHandler<NativeNotificationActivatedEventArgs> subscriber in
                 handler.GetInvocationList())
        {
            try
            {
                subscriber(this, activation);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // This runs on the Windows App SDK callback. A consumer failure
                // must not escape into the native dispatcher or starve the
                // remaining activation consumers.
                Asura.Application.SecretSafeDiagnosticProjection.WriteStandardError(
                    "notifications.windows-subscriber.failed",
                    exception);
            }
        }
    }

    private static IWindowsAppNotificationClient CreateClient()
    {
#if WINDOWS
        return new WindowsAppNotificationClient();
#else
        throw new PlatformNotSupportedException(
            "Windows app notifications require a Windows desktop build.");
#endif
    }
}

internal interface IWindowsAppNotificationClient : IDisposable
{
    event EventHandler<string>? Invoked;

    void Register();

    void Show(WindowsAppNotificationContent content);
}

internal sealed record WindowsAppNotificationContent(
    string Title,
    string Body,
    DateTimeOffset TimestampUtc,
    string ActivationPayload);
