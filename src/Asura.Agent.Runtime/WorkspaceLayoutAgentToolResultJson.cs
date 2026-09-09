using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class WorkspaceLayoutAgentToolResultJson
{
    public const string OutcomeUnknownStableCode =
        "workspace_layout_outcome_unknown";

    public static string SuccessStableCode(
        AgentWorkspaceLayoutReceipt receipt) => receipt.Operation switch
        {
            BuiltInAgentTools.TabCreate => "tab_created",
            BuiltInAgentTools.TabClose => "tab_closed",
            BuiltInAgentTools.PanelAdd => "panel_added",
            BuiltInAgentTools.PanelSplit => "panel_split",
            BuiltInAgentTools.PanelClose => "panel_closed",
            BuiltInAgentTools.ConnectionsList => "connections_listed",
            BuiltInAgentTools.PanelConnect => "panel_connected",
            _ => "workspace_layout_changed",
        };

    public static string Success(AgentWorkspaceLayoutReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        writer.WriteString("operation", receipt.Operation);
        writer.WriteString("window_id", receipt.WindowId.Value);
        writer.WriteString("workspace_id", receipt.WorkspaceId.Value);
        writer.WriteNumber("workspace_revision", receipt.WorkspaceRevision);
        writer.WriteNumber("graph_sequence", receipt.GraphSequence);
        WriteOptional(writer, "tab_id", receipt.TabId?.Value);
        WriteOptional(writer, "panel_id", receipt.PanelId?.Value);
        WriteOptional(
            writer,
            "panel_kind",
            receipt.PanelKind is { } kind
                ? WorkspaceLayoutAgentToolSet.PanelKindName(kind)
                : null);
        if (receipt.PanelId is not null)
        {
            writer.WriteBoolean("panel_ready", receipt.IsPanelReady);
        }
        if (string.Equals(receipt.Operation, BuiltInAgentTools.ConnectionsList, StringComparison.Ordinal))
        {
            writer.WriteStartArray("connections");
            foreach (var connection in receipt.Connections.Take(64))
            {
                writer.WriteStartObject();
                writer.WriteString("connection_ref", connection.Reference);
                writer.WriteString("name", connection.Name);
                writer.WriteString("kind", connection.Kind);
                writer.WriteStartArray("supported_panel_kinds");
                foreach (var supportedKind in connection.SupportedPanelKinds)
                {
                    writer.WriteStringValue(
                        WorkspaceLayoutAgentToolSet.PanelKindName(supportedKind));
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Failure(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var stableCode = ProviderStableCode(error);
        if (string.Equals(stableCode, OutcomeUnknownStableCode, StringComparison.Ordinal))
        {
            return AgentToolResultJson.Failure(stableCode, retryable: false);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", false);
        writer.WriteString("error", stableCode);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string ProviderStableCode(HostError error) =>
        error.StableCode switch
        {
            "cancelled" => "cancelled",
            "capability_not_supported" => "tool_not_available",
            "revision_conflict" => "target_changed",
            "not_found" => "target_changed",
            "target_changed" => "target_changed",
            "workspace_layout_rejected" => "workspace_layout_rejected",
            "workspace_layout_unsaved_changes" =>
                "workspace_layout_unsaved_changes",
            "workspace_panel_startup_failed" =>
                "workspace_panel_startup_failed",
            "workspace_connections_failed" => "workspace_connections_failed",
            OutcomeUnknownStableCode => OutcomeUnknownStableCode,
            _ => "workspace_layout_failed",
        };

    private static void WriteOptional(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, value);
    }
}
