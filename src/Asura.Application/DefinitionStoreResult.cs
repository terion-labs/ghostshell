namespace Asura.Application;

public sealed class DefinitionStoreResult<T>
{
    private DefinitionStoreResult(T? value, DefinitionStoreError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public DefinitionStoreError? Error { get; }

    public static DefinitionStoreResult<T> Success(T value) => new(value, null);

    public static DefinitionStoreResult<T> Failure(DefinitionStoreError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
