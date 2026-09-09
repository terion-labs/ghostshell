namespace Asura.Mcp;

/// <summary>
/// Reports only bounded shape metadata. Raw stderr is drained but never retained,
/// because a server may write credentials or tool content there.
/// </summary>
internal sealed record McpStderrDiagnostics(
    int ObservedByteCount,
    int ObservedLineCount,
    bool WasTruncated,
    bool ReadFailed);
