namespace Asura.Application;

public sealed class DiagnosticsBundleResult<T>
{
    private DiagnosticsBundleResult(T? value, DiagnosticsBundleError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public DiagnosticsBundleError? Error { get; }

    public static DiagnosticsBundleResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    public static DiagnosticsBundleResult<T> Failure(DiagnosticsBundleError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
