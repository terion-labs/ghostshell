namespace Asura.Mcp;

internal static class McpStreamableHttpClient
{
    public static Task<McpResult<McpClientSession>> ConnectAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        McpClientInfo clientInfo,
        McpSessionOptions? options = null,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(clientInfo);
        options ??= new McpSessionOptions();
        options.Validate();
        return McpClientSession.ConnectAsync(
            new BoundedStreamableHttpClientTransport(
                endpoint,
                headers,
                options,
                handler),
            clientInfo,
            options,
            cancellationToken);
    }
}
