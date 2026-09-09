namespace Asura.Protocol;

public sealed record ProtocolError(
    string Code,
    string Message,
    bool Retryable = false,
    IReadOnlyDictionary<string, string>? Details = null);
