namespace Asura.Application;

public sealed class AgentSessionCheckpointStoreResult<T>
{
    private AgentSessionCheckpointStoreResult(
        T? value,
        AgentSessionCheckpointStoreError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public AgentSessionCheckpointStoreError? Error { get; }

    public static AgentSessionCheckpointStoreResult<T> Success(T value) =>
        new(value, null);

    public static AgentSessionCheckpointStoreResult<T> Failure(
        AgentSessionCheckpointStoreError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
