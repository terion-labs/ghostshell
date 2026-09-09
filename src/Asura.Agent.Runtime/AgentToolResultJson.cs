using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Core;

namespace Asura.Agent.Runtime;

/// <summary>
/// Writes the common, secret-free fields shared by every provider-facing tool
/// result. Domain-specific payloads remain in their terminal/browser modules.
/// </summary>
internal static class AgentToolResultJson
{
    internal const int MaximumPanelIdBytes = 256;

    public static string Failure(
        string stableCode,
        bool retryable,
        PanelInstanceId? panelId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableCode);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", false);
        WritePanelId(writer, panelId);
        WriteError(writer, "error", stableCode, retryable);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string ReconciliationRequired(string causeStableCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(causeStableCode);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", false);
        WriteError(
            writer,
            "error",
            "tool_batch_reconciliation_required",
            retryable: true);
        writer.WriteString("caused_by", causeStableCode);
        writer.WriteString("required_action", "inspect_live_state");
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void WritePanelId(
        Utf8JsonWriter writer,
        PanelInstanceId? panelId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (panelId is { } value)
        {
            if (value.Value.Any(char.IsControl)
                || Encoding.UTF8.GetByteCount(value.Value)
                    > MaximumPanelIdBytes)
            {
                throw new ArgumentException(
                    "A provider-facing panel identifier must be printable "
                    + $"and at most {MaximumPanelIdBytes} UTF-8 bytes.",
                    nameof(panelId));
            }

            writer.WriteString("panel_id", value.Value);
        }
    }

    public static void WriteError(
        Utf8JsonWriter writer,
        string propertyName,
        string stableCode,
        bool retryable)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableCode);
        writer.WriteStartObject(propertyName);
        writer.WriteString("code", stableCode);
        writer.WriteBoolean("retryable", retryable);
        writer.WriteEndObject();
    }
}
