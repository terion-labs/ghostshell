using System.Buffers;
using System.Text.Json;

namespace Asura.Mcp;

internal static class McpAgentSchemaSanitizer
{
    private static readonly HashSet<string> AnnotationNames = new(
        [
            "$comment",
            "default",
            "deprecated",
            "description",
            "examples",
            "readOnly",
            "title",
            "writeOnly",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> SchemaMapNames = new(
        [
            "$defs",
            "definitions",
            "dependentSchemas",
            "patternProperties",
            "properties",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> SchemaArrayNames = new(
        [
            "allOf",
            "anyOf",
            "oneOf",
            "prefixItems",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> SchemaValueNames = new(
        [
            "additionalProperties",
            "contains",
            "contentSchema",
            "else",
            "if",
            "items",
            "not",
            "propertyNames",
            "then",
            "unevaluatedItems",
            "unevaluatedProperties",
        ],
        StringComparer.Ordinal);

    public static JsonElement Sanitize(
        JsonElement schema,
        McpSecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteSchema(writer, schema, redactor);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteSchema(
        Utf8JsonWriter writer,
        JsonElement schema,
        McpSecretRedactor redactor)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            WriteValue(writer, schema, redactor);
            return;
        }

        writer.WriteStartObject();
        var properties = schema.EnumerateObject()
            .Where(property => !AnnotationNames.Contains(property.Name))
            .ToArray();
        var projectedNames = ProjectPropertyNames(
            properties,
            redactor,
            "redacted_schema_keyword");
        for (var propertyIndex = 0;
             propertyIndex < properties.Length;
             propertyIndex++)
        {
            var property = properties[propertyIndex];
            writer.WritePropertyName(projectedNames[propertyIndex]);
            if (SchemaMapNames.Contains(property.Name)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                WriteSchemaMap(writer, property.Value, redactor);
            }
            else if (SchemaArrayNames.Contains(property.Name)
                && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in property.Value.EnumerateArray())
                {
                    WriteSchema(writer, item, redactor);
                }

                writer.WriteEndArray();
            }
            else if (SchemaValueNames.Contains(property.Name))
            {
                WriteSchema(writer, property.Value, redactor);
            }
            else
            {
                WriteValue(writer, property.Value, redactor);
            }

        }

        writer.WriteEndObject();
    }

    private static void WriteSchemaMap(
        Utf8JsonWriter writer,
        JsonElement map,
        McpSecretRedactor redactor)
    {
        writer.WriteStartObject();
        var properties = map.EnumerateObject().ToArray();
        var projectedNames = ProjectPropertyNames(
            properties,
            redactor,
            "redacted_property");
        for (var index = 0; index < properties.Length; index++)
        {
            writer.WritePropertyName(projectedNames[index]);
            WriteSchema(
                writer,
                properties[index].Value,
                redactor);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(
        Utf8JsonWriter writer,
        JsonElement value,
        McpSecretRedactor redactor)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject().ToArray();
                var projectedNames = ProjectPropertyNames(
                    properties,
                    redactor,
                    "redacted_property");
                for (var propertyIndex = 0;
                     propertyIndex < properties.Length;
                     propertyIndex++)
                {
                    writer.WritePropertyName(
                        projectedNames[propertyIndex]);
                    WriteValue(
                        writer,
                        properties[propertyIndex].Value,
                        redactor);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteValue(writer, item, redactor);
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

    private static string[] ProjectPropertyNames(
        IReadOnlyList<JsonProperty> properties,
        McpSecretRedactor redactor,
        string placeholderPrefix)
    {
        var projected = new string[properties.Count];
        var used = new HashSet<string>(
            properties.Count,
            StringComparer.Ordinal);
        for (var index = 0; index < properties.Count; index++)
        {
            var name = redactor.Redact(
                properties[index].Name,
                out var redacted);
            if (!redacted)
            {
                if (!used.Add(name))
                {
                    throw new InvalidOperationException(
                        "The MCP schema contains duplicate properties.");
                }

                projected[index] = name;
            }
        }

        for (var index = 0; index < properties.Count; index++)
        {
            if (projected[index] is not null)
            {
                continue;
            }

            var ordinal = index;
            string placeholder;
            do
            {
                placeholder = $"{placeholderPrefix}_{ordinal}";
                ordinal++;
            }
            while (!used.Add(placeholder));
            projected[index] = placeholder;
        }

        return projected;
    }
}
