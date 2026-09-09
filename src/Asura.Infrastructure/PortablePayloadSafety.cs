using System.Text.Json;

namespace Asura.Infrastructure;

internal static class PortablePayloadSafety
{
    private const int MaximumPayloadBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> SecretValuePropertyNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "passphrase",
        "privateKey",
        "apiKey",
        "accessToken",
        "refreshToken",
        "secretValue",
        "credentialValue",
    };

    public static bool TryValidate(string payloadJson, out string? error)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > MaximumPayloadBytes)
        {
            error = "The definition payload exceeds the portable size limit.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                payloadJson,
                new JsonDocumentOptions
                {
                    AllowDuplicateProperties = false,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "A definition payload must be a JSON object.";
                return false;
            }

            if (ContainsSecretValueProperty(document.RootElement))
            {
                error = "A definition payload contains a field reserved for secret material.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException)
        {
            error = "The definition payload is not valid JSON.";
            return false;
        }
    }

    private static bool ContainsSecretValueProperty(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (SecretValuePropertyNames.Contains(property.Name)
                        || ContainsSecretValueProperty(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsSecretValueProperty(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }
}
