namespace Asura.Files;

public sealed class FileProviderResult<T>
{
    private FileProviderResult(T? value, FileProviderError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public FileProviderError? Error { get; }

    public static FileProviderResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    public static FileProviderResult<T> Failure(FileProviderError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
