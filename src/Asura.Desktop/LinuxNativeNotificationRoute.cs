using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Core;

namespace Asura.Desktop;

/// <summary>
/// Encodes the shell destination as the portal action target. No process-local
/// lookup table is needed, so notification delivery cannot grow retained state.
/// </summary>
internal static class LinuxNativeNotificationRoute
{
    public static string Serialize(
        NativeNotificationRoute route,
        PanelNotificationKind kind = PanelNotificationKind.Notification)
    {
        ArgumentNullException.ThrowIfNull(route);
        var payload = new RoutePayload(
            route.WorkspaceId.Value,
            route.TabId?.Value,
            route.PanelId?.Value,
            kind);
        return JsonSerializer.Serialize(
            payload,
            LinuxNativeNotificationJsonContext.Default.RoutePayload);
    }

    public static bool TryDeserialize(
        string payload,
        out NativeNotificationRoute? route)
    {
        return TryDeserialize(payload, out route, out _);
    }

    public static bool TryDeserialize(
        string payload,
        out NativeNotificationRoute? route,
        out PanelNotificationKind kind)
    {
        route = null;
        kind = PanelNotificationKind.Notification;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(
                payload,
                LinuxNativeNotificationJsonContext.Default.RoutePayload);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.WorkspaceId))
            {
                return false;
            }

            route = new NativeNotificationRoute(
                new WorkspaceInstanceId(parsed.WorkspaceId),
                string.IsNullOrWhiteSpace(parsed.TabId)
                    ? null
                    : new TabInstanceId(parsed.TabId),
                string.IsNullOrWhiteSpace(parsed.PanelId)
                    ? null
                    : new PanelInstanceId(parsed.PanelId));
            kind = Enum.IsDefined(parsed.Kind)
                ? parsed.Kind
                : PanelNotificationKind.Notification;
            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or NotSupportedException
            or ArgumentException)
        {
            return false;
        }
    }

    internal sealed record RoutePayload(
        string WorkspaceId,
        string? TabId,
        string? PanelId,
        PanelNotificationKind Kind);
}

[JsonSerializable(typeof(LinuxNativeNotificationRoute.RoutePayload))]
internal sealed partial class LinuxNativeNotificationJsonContext : JsonSerializerContext;
