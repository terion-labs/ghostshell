using Asura.Core;

namespace Asura.Application;

public sealed record WorkspaceGraphSnapshot
{
    public WorkspaceGraphSnapshot(
        WindowInstanceId windowId,
        WorkspaceInstance workspace,
        long revision,
        long lastSequence)
    {
        WorkspaceGraphContractValidation.RequireId(windowId.Value, nameof(windowId));

        ArgumentNullException.ThrowIfNull(workspace);
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (lastSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastSequence));
        }

        WindowId = windowId;
        Workspace = new WorkspaceInstance(workspace);
        Revision = revision;
        LastSequence = lastSequence;
    }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstance Workspace { get; }

    public long Revision { get; }

    public long LastSequence { get; }
}
