using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Application;

namespace Asura.Mcp;

internal static class McpProviderResultProjection
{
    public static AgentMcpToolCallReceipt Project(
        McpToolCallResult result,
        McpSecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(redactor);
        var projection = Write(result, redactor, summarize: false);
        if (projection.Length
            > AgentMcpToolCallReceipt.MaximumProviderJsonBytes)
        {
            projection = Write(result, redactor, summarize: true);
        }

        return new AgentMcpToolCallReceipt(
            Encoding.UTF8.GetString(projection),
            result.IsError);
    }

    private static byte[] Write(
        McpToolCallResult result,
        McpSecretRedactor redactor,
        bool summarize)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", !result.IsError);
        writer.WriteString(
            "content_origin",
            AgentMcpToolCallReceipt.ContentOrigin);
        writer.WriteBoolean("is_error", result.IsError);
        writer.WriteStartArray("content");
        var omitted = 0;
        var redactedCount = 0;
        if (!summarize)
        {
            foreach (var block in result.Content)
            {
                if (!string.Equals(
                        block.Type,
                        "text",
                        StringComparison.Ordinal)
                    || block.Value.ValueKind != JsonValueKind.Object
                    || !block.Value.TryGetProperty(
                        "text",
                        out var textElement)
                    || textElement.ValueKind != JsonValueKind.String)
                {
                    omitted++;
                    continue;
                }

                var text = redactor.Redact(
                    textElement.GetString() ?? string.Empty,
                    out var redacted);
                if (redacted)
                {
                    redactedCount++;
                }

                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text);
                writer.WriteBoolean("redacted", redacted);
                writer.WriteEndObject();
            }
        }
        else
        {
            omitted = result.Content.Count;
        }

        writer.WriteEndArray();
        writer.WriteNumber("omitted_content_count", omitted);
        writer.WriteNumber("redacted_content_count", redactedCount);
        if (summarize)
        {
            writer.WriteString(
                "projection_notice",
                "The MCP result exceeded the provider projection limit; "
                + "content was omitted after the completed call.");
            writer.WriteNull("structured_content");
        }
        else if (result.StructuredContent is { } structured)
        {
            writer.WritePropertyName("structured_content");
            WriteJson(writer, structured, redactor);
        }
        else
        {
            writer.WriteNull("structured_content");
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteJson(
        Utf8JsonWriter writer,
        JsonElement value,
        McpSecretRedactor redactor)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject().ToArray();
                var projectedNames = new string[properties.Length];
                var usedNames = new HashSet<string>(
                    properties.Length,
                    StringComparer.Ordinal);
                for (var index = 0; index < properties.Length; index++)
                {
                    var name = redactor.Redact(
                        properties[index].Name,
                        out var redacted);
                    if (!redacted)
                    {
                        if (!usedNames.Add(name))
                        {
                            throw new InvalidOperationException(
                                "The MCP result contains duplicate properties.");
                        }

                        projectedNames[index] = name;
                    }
                }

                for (var index = 0; index < properties.Length; index++)
                {
                    if (projectedNames[index] is not null)
                    {
                        continue;
                    }

                    var ordinal = index;
                    string placeholder;
                    do
                    {
                        placeholder = $"redacted_property_{ordinal}";
                        ordinal++;
                    }
                    while (!usedNames.Add(placeholder));
                    projectedNames[index] = placeholder;
                }

                for (var index = 0; index < properties.Length; index++)
                {
                    writer.WritePropertyName(projectedNames[index]);
                    WriteJson(
                        writer,
                        properties[index].Value,
                        redactor);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteJson(writer, item, redactor);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(
                    redactor.Redact(
                        value.GetString() ?? string.Empty,
                        out _));
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                var scalar = redactor.Redact(
                    value.GetRawText(),
                    out var scalarRedacted);
                if (scalarRedacted)
                {
                    writer.WriteStringValue(scalar);
                }
                else
                {
                    value.WriteTo(writer);
                }

                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
