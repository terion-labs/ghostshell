using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Persists only durable Quick Terminal identity: tab order, selected tab, and
/// connection definitions. Shell processes and scrollback cannot survive an
/// application exit; restore reconnects them through the normal runtime path.
/// </summary>
internal static class QuickTerminalRecoveryCodec
{
    public const string SnapshotKey = "desktop.quick-terminal";
    public const int SchemaVersion = 1;

    private const int MaximumTabs = WorkspaceInstance.MaximumPanelCount;

    public static string Serialize(QuickTerminalViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var payload = new QuickTerminalRecoveryPayload(
            [.. viewModel.Tabs.Select(tab => tab.ConnectionId?.Value)],
            Math.Max(0, viewModel.Tabs.IndexOf(viewModel.ActiveTab!)),
            [.. viewModel.Tabs.Select(tab => tab.Title)],
            [.. viewModel.Tabs.Select(tab => tab.Icon)]);
        return JsonSerializer.Serialize(
            payload,
            QuickTerminalRecoveryJsonContext.Default.QuickTerminalRecoveryPayload);
    }

    public static bool TryDeserialize(
        RuntimeRecoverySnapshot snapshot,
        out QuickTerminalRecoveryPayload? payload)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        payload = null;
        if (!string.Equals(snapshot.Key, SnapshotKey, StringComparison.Ordinal) || snapshot.SchemaVersion != SchemaVersion)
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize(
                snapshot.PayloadJson,
                QuickTerminalRecoveryJsonContext.Default.QuickTerminalRecoveryPayload);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return false;
        }

        if (payload?.ConnectionIds is not { Length: > 0 and <= MaximumTabs } connectionIds
            || payload.ActiveTabIndex < 0
            || payload.ActiveTabIndex >= connectionIds.Length
            || payload.Titles is { } titles
                && (titles.Length != connectionIds.Length
                    || titles.Any(title => !IsDisplayText(title, 256)))
            || payload.Icons is { } icons
                && (icons.Length != connectionIds.Length
                    || icons.Any(icon => !IsDisplayText(icon, 64)))
            || connectionIds.Any(id => id is not null
                && (string.IsNullOrWhiteSpace(id)
                    || id.Length > 256
                    || id.Any(char.IsControl))))
        {
            payload = null;
            return false;
        }

        return true;
    }

    private static bool IsDisplayText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);
}

internal sealed record QuickTerminalRecoveryPayload(
    string?[] ConnectionIds,
    int ActiveTabIndex,
    string[]? Titles = null,
    string[]? Icons = null);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(QuickTerminalRecoveryPayload))]
internal sealed partial class QuickTerminalRecoveryJsonContext : JsonSerializerContext;
