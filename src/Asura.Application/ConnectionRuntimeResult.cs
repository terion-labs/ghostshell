namespace Asura.Application;

public abstract record ConnectionRuntimeResult<T>
{
    private ConnectionRuntimeResult()
    {
    }

    public sealed record Success(T Value) : ConnectionRuntimeResult<T>;

    public sealed record Failure(ConnectionRuntimeError Error) : ConnectionRuntimeResult<T>;

    public static ConnectionRuntimeResult<T> Succeed(T value) => new Success(value);

    public static ConnectionRuntimeResult<T> Fail(ConnectionRuntimeError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Failure(error);
    }
}
