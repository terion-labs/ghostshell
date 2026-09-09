namespace Asura.Mcp;

internal sealed class McpResult<T>
{
    private McpResult(T? value, McpError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public McpError? Error { get; }

    public static McpResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    public static McpResult<T> Failure(McpError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
