namespace Asura.Application;

public sealed class MonitorPanelResult<T>
{
    private MonitorPanelResult(T? value, MonitorPanelError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public MonitorPanelError? Error { get; }

    public static MonitorPanelResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    public static MonitorPanelResult<T> Failure(MonitorPanelError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
