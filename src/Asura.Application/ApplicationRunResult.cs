namespace Asura.Application;

public sealed class ApplicationRunResult<T>
{
    private ApplicationRunResult(T? value, ApplicationRunError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public ApplicationRunError? Error { get; }

    public static ApplicationRunResult<T> Success(T value) => new(value, null);

    public static ApplicationRunResult<T> Failure(ApplicationRunError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
