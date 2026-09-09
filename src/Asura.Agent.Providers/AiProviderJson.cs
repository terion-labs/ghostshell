using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Providers;

internal static class AiProviderJson
{
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    public static byte[] Write(
        int maximumBytes,
        Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var stream = new BoundedMemoryStream(maximumBytes);
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       // Provider payloads are UTF-8 JSON, never HTML. Preserve
                       // tool and page text instead of exposing \uXXXX escapes.
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       Indented = false,
                       MaxDepth = 64,
                       SkipValidation = false,
                   }))
        {
            write(writer);
        }

        return stream.ToArray();
    }

    public static JsonDocument Parse(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            return JsonDocument.Parse(utf8Json, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError,
                innerException: exception);
        }
    }

    public static string RequiredBoundedString(
        JsonElement parent,
        string propertyName,
        int maximumLength)
    {
        if (!parent.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
        }

        var value = property.GetString()!;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
        }

        return value;
    }

    public static string? OptionalBoundedString(
        JsonElement parent,
        string propertyName,
        int maximumLength)
    {
        if (!parent.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
        }

        var value = property.GetString()!;
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
        }

        return value;
    }

    public static JsonElement RequiredObject(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Object)
        {
            throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
        }

        return property;
    }

    public static JsonElement RequiredArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
        }

        return property;
    }

    public static string ToolResultContent(AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var maximumBytes = checked(
            (AgentToolResultValue.MaximumContentBytes * 6) + 1024);
        var utf8 = Write(
            maximumBytes,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean(
                    "ok",
                    result.Status == AgentToolResultStatus.Succeeded);
                writer.WriteString("code", result.StableCode);
                writer.WriteString(
                    "value_kind",
                    result.Value.Kind == AgentToolResultValueKind.Json
                        ? "json"
                        : "text");
                writer.WritePropertyName("value");
                if (result.Value.Kind == AgentToolResultValueKind.Json)
                {
                    using var value = JsonDocument.Parse(
                        result.Value.Content,
                        DocumentOptions);
                    value.RootElement.WriteTo(writer);
                }
                else
                {
                    writer.WriteStringValue(result.Value.Content);
                }

                writer.WriteEndObject();
            });
        return Encoding.UTF8.GetString(utf8);
    }
}
