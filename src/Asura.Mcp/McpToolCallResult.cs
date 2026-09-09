using System.Text.Json;

namespace Asura.Mcp;

internal sealed record McpToolCallResult(
    IReadOnlyList<McpToolCallContent> Content,
    JsonElement? StructuredContent,
    bool IsError);
