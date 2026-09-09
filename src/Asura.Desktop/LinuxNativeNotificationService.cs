using Asura.Application;
using Tmds.DBus.Protocol;

namespace Asura.Desktop;

/// <summary>
/// Publishes Linux desktop notifications through the XDG Desktop Portal and
/// routes clicks received while this process is running back into Asura.
/// </summary>
public sealed class LinuxNativeNotificationService :
    INativeNotificationService,
    IDisposable
{
    internal const string DefaultAction = "asura.open-notification";

    private readonly object _lifetimeGate = new();
    private readonly ILinuxPortalNotificationClient _client;
    private Task? _initialization;
    private IDisposable? _subscription;
    private bool _disposed;

    public LinuxNativeNotificationService()
        : this(new LinuxPortalNotificationClient())
    {
    }

    internal LinuxNativeNotificationService(ILinuxPortalNotificationClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public event EventHandler<NativeNotificationActivatedEventArgs>? Activated;

    internal Task Initialization => EnsureInitialized();

    public async ValueTask ShowAsync(
        NativeNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Id);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureInitialized().WaitAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();
        await _client.AddNotificationAsync(
                notification.Id,
                CreatePortalNotification(notification))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        IDisposable? subscription;
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscription = _subscription;
            _subscription = null;
        }

        subscription?.Dispose();
        _client.Dispose();
    }

    internal static Dictionary<string, VariantValue> CreatePortalNotification(
        NativeNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var payload = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["title"] = notification.Title,
            ["body"] = notification.Body,
            ["priority"] = Priority(notification.Kind),
            ["default-action"] = DefaultAction,
            ["default-action-target"] = VariantValue.Variant(
                LinuxNativeNotificationRoute.Serialize(
                    notification.Route,
                    notification.Kind)),
        };
        return payload;
    }

    private async Task InitializeAsync()
    {
        var subscription = await _client
            .WatchActionInvokedAsync(OnActionInvoked)
            .ConfigureAwait(false);
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                subscription.Dispose();
                return;
            }

            _subscription = subscription;
        }
    }

    private void OnActionInvoked(
        Exception? exception,
        LinuxPortalNotificationAction action)
    {
        if (exception is not null)
        {
            ReportCallbackFailure(exception);
            return;
        }

        if (IsDisposed()
            || string.IsNullOrWhiteSpace(action.Id)
            || string.IsNullOrWhiteSpace(action.Action))
        {
            return;
        }

        var payload = ReadRoutePayload(action.Parameters);
        if (payload is null
            || !LinuxNativeNotificationRoute.TryDeserialize(
                payload,
                out var route,
                out var kind)
            || route is null)
        {
            return;
        }

        PublishActivation(new NativeNotificationActivatedEventArgs(
            action.Id,
            route,
            action.Action,
            kind,
            ReadActivationToken(action.Parameters)));
    }

    private static string? ReadRoutePayload(VariantValue[] parameters)
    {
        if (parameters.Length == 0)
        {
            return null;
        }

        try
        {
            var target = parameters[0];
            while (target.Type == VariantValueType.Variant)
            {
                target = target.GetVariantValue();
            }

            return target.Type == VariantValueType.String
                ? target.GetString()
                : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidCastException
            or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadActivationToken(VariantValue[] parameters)
    {
        if (parameters.Length < 2)
        {
            return null;
        }

        try
        {
            var platformData = Unwrap(parameters[1]);
            if (platformData.Type != VariantValueType.Dictionary)
            {
                return null;
            }

            var values = platformData.GetDictionary<string, VariantValue>();
            return values.TryGetValue("activation-token", out var token)
                && Unwrap(token) is { Type: VariantValueType.String } value
                    ? value.GetString()
                    : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidCastException
            or ArgumentException)
        {
            return null;
        }
    }

    private void PublishActivation(NativeNotificationActivatedEventArgs eventArgs)
    {
        var handlers = Activated;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<NativeNotificationActivatedEventArgs> handler
                 in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                // Never let application code unwind through Tmds.DBus. A value
                // handler exception disconnects the shared session-bus connection.
                ReportCallbackFailure(exception);
            }
        }
    }

    private static VariantValue Unwrap(VariantValue value)
    {
        while (value.Type == VariantValueType.Variant)
        {
            value = value.GetVariantValue();
        }

        return value;
    }

    private static void ReportCallbackFailure(Exception exception) =>
        Asura.Application.SecretSafeDiagnosticProjection.WriteStandardError(
            "notifications.linux-callback.failed",
            exception);

    private static string Priority(PanelNotificationKind kind) => kind switch
    {
        PanelNotificationKind.AgentFailed or PanelNotificationKind.FileTransferFailed =>
            "high",
        PanelNotificationKind.Bell => "low",
        _ => "normal",
    };

    private bool IsDisposed()
    {
        lock (_lifetimeGate)
        {
            return _disposed;
        }
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed())
        {
            throw new ObjectDisposedException(nameof(LinuxNativeNotificationService));
        }
    }

    private Task EnsureInitialized()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _initialization ??= InitializeAsync();
        }
    }
}
