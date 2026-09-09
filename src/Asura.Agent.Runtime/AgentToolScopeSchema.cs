using System.Buffers;
using System.Text.Json;
using Asura.Agent;

namespace Asura.Agent.Runtime;

/// <summary>
/// Adds the stable workspace-level panel selector to a panel tool. Live panel
/// identifiers deliberately never appear in provider schemas: they are data
/// returned by workspace tools and are revalidated when the tool executes.
/// </summary>
internal static class AgentToolScopeSchema
{
    public static AgentToolDefinition WithRequiredPanelId(
        AgentToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        var wroteProperties = false;
        var wroteRequired = false;
        foreach (var property in tool.InputSchema.EnumerateObject())
        {
            switch (property.Name)
            {
                case "properties":
                    wroteProperties = true;
                    writer.WriteStartObject("properties");
                    foreach (var inputProperty in property.Value.EnumerateObject())
                    {
                        inputProperty.WriteTo(writer);
                    }

                    writer.WriteStartObject("panel_id");
                    writer.WriteString("type", "string");
                    writer.WriteNumber("minLength", 1);
                    writer.WriteNumber("maxLength", 128);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                case "required":
                    wroteRequired = true;
                    writer.WriteStartArray("required");
                    foreach (var required in property.Value.EnumerateArray())
                    {
                        required.WriteTo(writer);
                    }

                    writer.WriteStringValue("panel_id");
                    writer.WriteEndArray();
                    break;
                default:
                    property.WriteTo(writer);
                    break;
            }
        }

        if (!wroteProperties || !wroteRequired)
        {
            throw new InvalidOperationException(
                "Agent tool schemas require properties and required members.");
        }

        writer.WriteEndObject();
        writer.Flush();
        return new AgentToolDefinition(
            tool.Name,
            tool.Description,
            buffer.WrittenSpan.ToArray());
    }
}
