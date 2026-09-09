namespace Asura.Mcp;

internal sealed class McpTransportFailureException(
    McpErrorCode code,
    string message,
    Exception? innerException = null) : IOException(message, innerException)
{
    public McpErrorCode Code { get; } = code;

}
