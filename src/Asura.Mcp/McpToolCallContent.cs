using System.Text.Json;

namespace Asura.Mcp;

/// <summary>
/// A bounded, validated MCP content block. The JSON remains untrusted server data.
/// </summary>
internal sealed record McpToolCallContent(string Type, JsonElement Value);
