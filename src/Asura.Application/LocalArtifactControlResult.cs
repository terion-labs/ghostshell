namespace Asura.Application;

public sealed class LocalArtifactControlResult<T>
{
    private LocalArtifactControlResult(T? value, LocalArtifactControlError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public LocalArtifactControlError? Error { get; }

    public static LocalArtifactControlResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    public static LocalArtifactControlResult<T> Failure(LocalArtifactControlError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
