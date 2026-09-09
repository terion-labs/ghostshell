using System.Text.Json;

namespace Asura.Mcp;

internal sealed record McpTool(
    string Name,
    JsonElement InputSchema);
