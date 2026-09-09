using System.Buffers;
using System.Text.Json;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class McpAgentToolResultJson
{
    internal const string ManifestChangedStableCode =
        "mcp_manifest_changed";

    internal const string OutcomeUnknownStableCode =
        "mcp_tool_outcome_unknown";

    public static string Failure(string stableCode)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", false);
        writer.WriteString("code", stableCode);
        writer.WriteBoolean("retryable", false);
        writer.WriteString(
            "content_origin",
            AgentMcpToolCallReceipt.ContentOrigin);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
