using System.Text;
using System.Text.Json;

namespace Asura.Mcp;

internal static class McpJsonBudget
{
    public static bool TryValidate(
        JsonElement value,
        int maxUtf8Bytes,
        int maxDepth,
        int maxNodes,
        bool requireObject,
        out string reason)
    {
        try
        {
            if (requireObject && value.ValueKind != JsonValueKind.Object)
            {
                reason = "The JSON value must be an object.";
                return false;
            }

            if (Encoding.UTF8.GetByteCount(value.GetRawText()) > maxUtf8Bytes)
            {
                reason = "The JSON value exceeds its byte limit.";
                return false;
            }

            var remainingNodes = maxNodes;
            if (!TryVisit(value, 1, maxDepth, ref remainingNodes))
            {
                reason = "The JSON value exceeds its depth or node limit, or contains duplicate properties.";
                return false;
            }
        }
        catch (InvalidOperationException)
        {
            reason = "The JSON value is unavailable.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool TryValidateDocument(
        ReadOnlyMemory<byte> utf8Json,
        int maxDepth,
        int maxNodes,
        out JsonDocument? document)
    {
        document = null;
        try
        {
            document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = maxDepth,
                });

            var remainingNodes = maxNodes;
            if (!TryVisit(document.RootElement, 1, maxDepth, ref remainingNodes))
            {
                document.Dispose();
                document = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            document?.Dispose();
            document = null;
            return false;
        }
    }

    private static bool TryVisit(
        JsonElement value,
        int depth,
        int maxDepth,
        ref int remainingNodes)
    {
        if (depth > maxDepth || --remainingNodes < 0)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)
                    || !TryVisit(property.Value, depth + 1, maxDepth, ref remainingNodes))
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (!TryVisit(item, depth + 1, maxDepth, ref remainingNodes))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
