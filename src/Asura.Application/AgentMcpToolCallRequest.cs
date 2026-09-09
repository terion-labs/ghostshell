using System.Text;
using System.Text.Json;

namespace Asura.Application;

/// <summary>
/// Exact arguments for one tool from a frozen MCP run manifest.
/// </summary>
public sealed record AgentMcpToolCallRequest
{
    public const int MaximumArgumentsBytes = 8 * 1024;
    public const int MaximumJsonDepth = 32;
    public const int MaximumJsonNodes = 16_384;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentMcpToolCallRequest(
        AgentMcpToolManifest manifest,
        JsonElement arguments)
    {
        Manifest = manifest
            ?? throw new ArgumentNullException(nameof(manifest));
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "MCP tool arguments must be one JSON object.",
                nameof(arguments));
        }

        string raw;
        try
        {
            raw = arguments.GetRawText();
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(
                "MCP tool arguments are unavailable.",
                nameof(arguments),
                exception);
        }

        try
        {
            if (StrictUtf8.GetByteCount(raw) > MaximumArgumentsBytes)
            {
                throw new ArgumentException(
                    "MCP tool arguments exceed their byte limit.",
                    nameof(arguments));
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "MCP tool arguments must contain valid Unicode.",
                nameof(arguments),
                exception);
        }

        var remainingNodes = MaximumJsonNodes;
        if (!TryValidateStructure(
                arguments,
                depth: 1,
                ref remainingNodes))
        {
            throw new ArgumentException(
                "MCP tool arguments exceed their structural limits, contain invalid Unicode, or contain duplicate fields.",
                nameof(arguments));
        }

        if (AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(raw)
            || ContainsLikelyLiteralSecret(arguments))
        {
            throw new ArgumentException(
                "MCP tool arguments cannot contain literal credentials; configure the server to receive secrets through its profile-scoped vault environment.",
                nameof(arguments));
        }

        Arguments = arguments.Clone();
    }

    public AgentMcpToolManifest Manifest { get; }

    public JsonElement Arguments { get; }

    private static bool TryValidateStructure(
        JsonElement value,
        int depth,
        ref int remainingNodes)
    {
        if (depth > MaximumJsonDepth || --remainingNodes < 0)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)
                    || !IsValidUnicode(property.Name)
                    || !TryValidateStructure(
                        property.Value,
                        depth + 1,
                        ref remainingNodes))
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (!TryValidateStructure(
                        item,
                        depth + 1,
                        ref remainingNodes))
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                return IsValidUnicode(value.GetString() ?? string.Empty);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidUnicode(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool ContainsLikelyLiteralSecret(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(
                            property.Name)
                        || property.Value.ValueKind == JsonValueKind.String
                        && AgentLiteralSecretValidator
                            .ContainsLikelyLiteralSecret(
                                property.Name
                                + "="
                                + (property.Value.GetString()
                                    ?? string.Empty))
                        || ContainsLikelyLiteralSecret(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                return value.EnumerateArray().Any(
                    ContainsLikelyLiteralSecret);
            case JsonValueKind.String:
                return AgentLiteralSecretValidator
                    .ContainsLikelyLiteralSecret(
                        value.GetString() ?? string.Empty);
            default:
                return false;
        }
    }
}
