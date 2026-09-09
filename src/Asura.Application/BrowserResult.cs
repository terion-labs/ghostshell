namespace Asura.Application;

public sealed class BrowserResult<T>
{
    private BrowserResult(T? value, BrowserError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public BrowserError? Error { get; }

    public static BrowserResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    public static BrowserResult<T> Failure(BrowserError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
