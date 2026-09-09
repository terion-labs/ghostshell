namespace Asura.Mcp;

internal enum McpErrorCode
{
    Cancelled,
    Disposed,
    LaunchFailed,
    TransportClosed,
    ProcessExited,
    TransportFailed,
    MessageTooLarge,
    InvalidMessage,
    UnsupportedProtocolVersion,
    MissingToolsCapability,
    RemoteError,
    InvalidResult,
    LimitExceeded,
    InvalidArguments,
    ToolNotListed,
    ToolCatalogStale,
}

internal sealed record McpError(
    McpErrorCode Code,
    string Message,
    int? RemoteCode = null,
    bool CleanupUncertain = false,
    bool OutcomeUncertain = false);
