namespace Asura.Application;

public abstract record SessionStreamItem
{
    private SessionStreamItem()
    {
    }

    public sealed record Event(SessionEvent Value) : SessionStreamItem;

    public sealed record ResynchronizationRequired(
        SessionSnapshot Snapshot,
        long ResumeAfterSequence) : SessionStreamItem;
}
