namespace Asura.Mcp;

internal sealed record McpServerInfo(
    string Name,
    string Version,
    bool ToolsListChanged);
