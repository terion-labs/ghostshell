using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class PanelAgentToolResultJson
{
    private const int MaximumUntrustedDisplayBytes = 128;

    public static string Success(
        AgentPanelActionResult result,
        PanelInstanceId panelId)
    {
        ArgumentNullException.ThrowIfNull(result);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        AgentToolResultJson.WritePanelId(writer, panelId);
        switch (result)
        {
            case AgentPanelActionResult.Inspected inspected:
                WriteInspection(writer, inspected.Panel);
                break;
            case AgentPanelActionResult.Focused focused:
                WriteFocusReceipt(writer, focused.Receipt);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result.GetType(),
                    "The panel action result kind is unsupported.");
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Failure(
        HostError error,
        PanelInstanceId panelId)
    {
        ArgumentNullException.ThrowIfNull(error);
        return AgentToolResultJson.Failure(
            error.StableCode,
            error.Retryable,
            panelId);
    }

    private static void WriteInspection(
        Utf8JsonWriter writer,
        AgentContextPanel panel)
    {
        writer.WriteString("content_origin", "untrusted_panel_metadata");
        writer.WriteString("window_id", panel.WindowId.Value);
        writer.WriteString("workspace_id", panel.WorkspaceId.Value);
        writer.WriteNumber("workspace_revision", panel.WorkspaceRevision);
        writer.WriteString("tab_id", panel.TabId.Value);
        writer.WriteString("session_id", panel.SessionId!.Value.Value);
        writer.WriteNumber(
            "session_revision",
            panel.SessionRevision
                ?? throw new ArgumentException(
                    "An inspected panel requires a live session revision.",
                    nameof(panel)));
        writer.WriteString("kind", PanelKindName(panel.Kind));
        writer.WriteString(
            "lifecycle",
            EnumName(
                panel.Lifecycle
                    ?? throw new ArgumentException(
                        "An inspected panel requires a lifecycle.",
                        nameof(panel))));
        writer.WriteString(
            "health",
            EnumName(
                panel.Health
                    ?? throw new ArgumentException(
                        "An inspected panel requires health state.",
                        nameof(panel))));
        writer.WriteBoolean("visible", panel.IsVisible);
        writer.WriteBoolean("focused", panel.IsFocused);
        writer.WriteBoolean("active_work", panel.HasActiveWork);

        var redactions = 0;
        WriteUntrusted(
            writer,
            "workspace_title",
            panel.WorkspaceTitle,
            ref redactions);
        WriteUntrusted(
            writer,
            "tab_title",
            panel.TabTitle,
            ref redactions);
        WriteUntrusted(
            writer,
            "panel_title",
            panel.PanelTitle,
            ref redactions);
        WriteUntrusted(
            writer,
            "connection_boundary",
            panel.ConnectionBoundary,
            ref redactions);
        WriteUntrusted(
            writer,
            "working_directory",
            panel.CurrentWorkingDirectory
                ?? panel.InitialWorkingDirectory,
            ref redactions);
        writer.WriteNumber("redactions", redactions);
    }

    private static void WriteFocusReceipt(
        Utf8JsonWriter writer,
        AgentPanelFocusReceipt receipt)
    {
        writer.WriteString("window_id", receipt.WindowId.Value);
        writer.WriteString("workspace_id", receipt.WorkspaceId.Value);
        writer.WriteString("tab_id", receipt.TabId.Value);
        writer.WriteNumber(
            "workspace_revision",
            receipt.WorkspaceRevision);
        writer.WriteNumber("graph_sequence", receipt.GraphSequence);
        writer.WriteBoolean("focused", true);
        writer.WriteBoolean("changed", receipt.Changed);
    }

    private static void WriteUntrusted(
        Utf8JsonWriter writer,
        string propertyName,
        string? value,
        ref int redactions)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        var redacted = TerminalContentRedactor.Redact(value);
        redactions = checked(redactions + redacted.RedactionCount);
        writer.WriteString(
            propertyName,
            TruncateUtf8(redacted.Text, MaximumUntrustedDisplayBytes));
    }

    private static string TruncateUtf8(
        string value,
        int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes - 3)
            {
                break;
            }

            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }

        builder.Append('…');
        return builder.ToString();
    }

    private static string PanelKindName(PanelKind kind) =>
        kind switch
        {
            PanelKind.Terminal => "terminal",
            PanelKind.Browser => "browser",
            PanelKind.FileViewer => "file_viewer",
            PanelKind.Statistics => "statistics",
            PanelKind.ProcessMonitor => "process_monitor",
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "The panel kind is unsupported."),
        };

    private static string EnumName<T>(T value)
        where T : struct, Enum =>
        value.ToString().ToLowerInvariant();
}
