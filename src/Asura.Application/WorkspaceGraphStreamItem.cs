namespace Asura.Application;

public abstract record WorkspaceGraphStreamItem
{
    private WorkspaceGraphStreamItem()
    {
    }

    public sealed record Event(WorkspaceGraphEvent Value) : WorkspaceGraphStreamItem;

    public sealed record ResynchronizationRequired(
        WorkspaceGraphSnapshot Snapshot,
        long ResumeAfterSequence) : WorkspaceGraphStreamItem;
}
