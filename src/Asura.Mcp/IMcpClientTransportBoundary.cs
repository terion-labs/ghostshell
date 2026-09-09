using ModelContextProtocol.Client;

namespace Asura.Mcp;

/// <summary>
/// Security boundary layered underneath the official SDK's MCP lifecycle.
/// Implementations bound inbound bytes and own transport-specific cleanup.
/// </summary>
internal interface IMcpClientTransportBoundary :
    IClientTransport,
    IAsyncDisposable
{
    bool CleanupUncertain { get; }

    McpStderrDiagnostics Diagnostics { get; }

    void ResetIncomingMessageBudget();
}
