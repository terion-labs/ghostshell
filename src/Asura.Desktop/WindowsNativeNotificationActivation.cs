using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Core;

namespace Asura.Desktop;

/// <summary>
/// Carries stable shell identities through the string-only Windows activation
/// argument without retaining process-local notification state.
/// </summary>
internal static class WindowsNativeNotificationActivation
{
    internal const string PayloadArgument = "asura";
    internal const string DefaultActionId = "open";

    private const int CurrentVersion = 1;
    private const int MaxPayloadBytes = 4 * 1024;
    private const int MaxArgumentsCharacters = 8 * 1024;

    public static string Serialize(
        NativeNotification notification,
        string actionId = DefaultActionId)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(notification.Route);
        if (!Enum.IsDefined(notification.Kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(notification),
                notification.Kind,
                "The native notification kind is not supported.");
        }

        var payload = new ActivationPayload(
            CurrentVersion,
            notification.Id,
            actionId,
            notification.Kind,
            notification.Route.WorkspaceId.Value,
            notification.Route.TabId?.Value,
            notification.Route.PanelId?.Value);
        var json = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            WindowsNativeNotificationJsonContext.Default.ActivationPayload);
        if (json.Length > MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"The native notification activation payload exceeds {MaxPayloadBytes} bytes.",
                nameof(notification));
        }

        return Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryParseArguments(
        string arguments,
        out NativeNotificationActivatedEventArgs? activation)
    {
        activation = null;
        if (string.IsNullOrWhiteSpace(arguments)
            || arguments.Length > MaxArgumentsCharacters)
        {
            return false;
        }

        var encodedPayload = FindPayload(arguments);
        if (encodedPayload is null
            || !TryDecode(encodedPayload, out var payload)
            || payload is null
            || payload.Version != CurrentVersion
            || string.IsNullOrWhiteSpace(payload.NotificationId)
            || string.IsNullOrWhiteSpace(payload.ActionId)
            || !Enum.IsDefined(payload.Kind)
            || string.IsNullOrWhiteSpace(payload.WorkspaceId))
        {
            return false;
        }

        try
        {
            var route = new NativeNotificationRoute(
                new WorkspaceInstanceId(payload.WorkspaceId),
                string.IsNullOrWhiteSpace(payload.TabId)
                    ? null
                    : new TabInstanceId(payload.TabId),
                string.IsNullOrWhiteSpace(payload.PanelId)
                    ? null
                    : new PanelInstanceId(payload.PanelId));
            activation = new NativeNotificationActivatedEventArgs(
                payload.NotificationId,
                route,
                payload.ActionId,
                payload.Kind);
            return true;
        }
        catch (ArgumentException)
        {
            activation = null;
            return false;
        }
    }

    private static string? FindPayload(string arguments)
    {
        string? payload = null;
        foreach (var argument in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = argument.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = argument.AsSpan(0, separator).TrimStart('?');
            if (!key.Equals(PayloadArgument, StringComparison.Ordinal))
            {
                continue;
            }

            if (payload is not null)
            {
                return null;
            }

            payload = argument[(separator + 1)..];
        }

        return payload;
    }

    private static bool TryDecode(
        string encodedPayload,
        out ActivationPayload? payload)
    {
        payload = null;
        if (encodedPayload.Length == 0
            || encodedPayload.Length > MaxArgumentsCharacters)
        {
            return false;
        }

        try
        {
            var base64 = encodedPayload
                .Replace('-', '+')
                .Replace('_', '/');
            var padding = base64.Length % 4;
            if (padding == 1)
            {
                return false;
            }

            if (padding > 0)
            {
                base64 = base64.PadRight(base64.Length + (4 - padding), '=');
            }

            var json = Convert.FromBase64String(base64);
            if (json.Length > MaxPayloadBytes)
            {
                return false;
            }

            payload = JsonSerializer.Deserialize(
                json,
                WindowsNativeNotificationJsonContext.Default.ActivationPayload);
            return payload is not null;
        }
        catch (Exception exception) when (exception is FormatException
            or JsonException
            or NotSupportedException)
        {
            return false;
        }
    }

    internal sealed record ActivationPayload(
        int Version,
        string NotificationId,
        string ActionId,
        PanelNotificationKind Kind,
        string WorkspaceId,
        string? TabId,
        string? PanelId);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WindowsNativeNotificationActivation.ActivationPayload))]
internal sealed partial class WindowsNativeNotificationJsonContext : JsonSerializerContext;
