namespace Asura.Application;

public abstract record HostResult<T>
{
    private HostResult()
    {
    }

    public sealed record Success(T Value, long ResultingRevision) : HostResult<T>;

    public sealed record Failure(HostError Error, long CurrentRevision) : HostResult<T>;

    public static HostResult<T> Succeed(T value, long resultingRevision) =>
        new Success(value, resultingRevision);

    public static HostResult<T> Fail(HostError error, long currentRevision) =>
        new Failure(error, currentRevision);
}
