namespace Asura.Mcp;

internal sealed record McpClientInfo
{
    public McpClientInfo(string name, string version)
    {
        Name = McpText.RequireIdentifier(name, 128, nameof(name));
        Version = McpText.RequireIdentifier(version, 128, nameof(version));
    }

    public string Name { get; }

    public string Version { get; }
}
