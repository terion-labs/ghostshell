using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;

namespace Asura.Mcp;

/// <summary>
/// Resolves only the SDK's generated MCP contracts. The returned metadata is
/// passed to every serializer call explicitly, so reflection can stay disabled.
/// </summary>
internal static class McpSdkJson
{
    public static JsonTypeInfo<T> TypeInfo<T>() =>
        ModelContextProtocol.McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T))
            is JsonTypeInfo<T> typeInfo
                ? typeInfo
                : throw new NotSupportedException(
                    $"The MCP SDK does not publish generated JSON metadata for {typeof(T).Name}.");
}
