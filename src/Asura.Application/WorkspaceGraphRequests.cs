using Asura.Core;

namespace Asura.Application;

public sealed record RegisterWorkspaceGraphRequest
{
    public RegisterWorkspaceGraphRequest(
        WindowInstanceId windowId,
        WorkspaceInstance workspace)
    {
        WorkspaceGraphContractValidation.RequireId(windowId.Value, nameof(windowId));
        ArgumentNullException.ThrowIfNull(workspace);
        WindowId = windowId;
        Workspace = new WorkspaceInstance(workspace);
    }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstance Workspace { get; }
}

public sealed record UnregisterWorkspaceGraphRequest
{
    public UnregisterWorkspaceGraphRequest(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId)
    {
        WorkspaceGraphContractValidation.RequireId(windowId.Value, nameof(windowId));
        WorkspaceGraphContractValidation.RequireId(workspaceId.Value, nameof(workspaceId));
        WindowId = windowId;
        WorkspaceId = workspaceId;
    }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstanceId WorkspaceId { get; }
}

public sealed record ActivateWorkspaceTabRequest
{
    public ActivateWorkspaceTabRequest(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId)
    {
        WorkspaceGraphContractValidation.RequireId(workspaceId.Value, nameof(workspaceId));
        WorkspaceGraphContractValidation.RequireId(tabId.Value, nameof(tabId));
        WorkspaceId = workspaceId;
        TabId = tabId;
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public TabInstanceId TabId { get; }
}

public sealed record ActivateWorkspacePanelRequest
{
    public ActivateWorkspacePanelRequest(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId)
    {
        WorkspaceGraphContractValidation.RequireId(workspaceId.Value, nameof(workspaceId));
        WorkspaceGraphContractValidation.RequireId(tabId.Value, nameof(tabId));
        WorkspaceGraphContractValidation.RequireId(panelId.Value, nameof(panelId));
        WorkspaceId = workspaceId;
        TabId = tabId;
        PanelId = panelId;
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public TabInstanceId TabId { get; }

    public PanelInstanceId PanelId { get; }
}

public sealed record WatchWorkspaceGraphRequest
{
    public WatchWorkspaceGraphRequest(
        WorkspaceInstanceId workspaceId,
        long afterSequence)
    {
        WorkspaceGraphContractValidation.RequireId(workspaceId.Value, nameof(workspaceId));

        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        WorkspaceId = workspaceId;
        AfterSequence = afterSequence;
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public long AfterSequence { get; }
}

public sealed record TransferWorkspaceTabRequest
{
    public TransferWorkspaceTabRequest(
        WindowInstanceId sourceWindowId,
        WorkspaceInstance source,
        long expectedSourceRevision,
        WindowInstanceId destinationWindowId,
        WorkspaceInstance destination,
        long expectedDestinationRevision,
        TabInstanceId tabId)
    {
        WorkspaceGraphTransferValidation.ValidateCommon(
            sourceWindowId,
            source,
            expectedSourceRevision,
            destinationWindowId,
            destination,
            expectedDestinationRevision);
        WorkspaceGraphContractValidation.RequireId(tabId.Value, nameof(tabId));
        SourceWindowId = sourceWindowId;
        Source = new WorkspaceInstance(source);
        ExpectedSourceRevision = expectedSourceRevision;
        DestinationWindowId = destinationWindowId;
        Destination = new WorkspaceInstance(destination);
        ExpectedDestinationRevision = expectedDestinationRevision;
        TabId = tabId;
    }

    public WindowInstanceId SourceWindowId { get; }
    public WorkspaceInstance Source { get; }
    public long ExpectedSourceRevision { get; }
    public WindowInstanceId DestinationWindowId { get; }
    public WorkspaceInstance Destination { get; }
    public long ExpectedDestinationRevision { get; }
    public TabInstanceId TabId { get; }
}

public sealed record TransferWorkspacePanelRequest
{
    public TransferWorkspacePanelRequest(
        WindowInstanceId sourceWindowId,
        WorkspaceInstance source,
        long expectedSourceRevision,
        TabInstanceId sourceTabId,
        WindowInstanceId destinationWindowId,
        WorkspaceInstance destination,
        long expectedDestinationRevision,
        TabInstanceId destinationTabId,
        PanelInstanceId panelId)
    {
        WorkspaceGraphTransferValidation.ValidateCommon(
            sourceWindowId,
            source,
            expectedSourceRevision,
            destinationWindowId,
            destination,
            expectedDestinationRevision);
        WorkspaceGraphContractValidation.RequireId(sourceTabId.Value, nameof(sourceTabId));
        WorkspaceGraphContractValidation.RequireId(destinationTabId.Value, nameof(destinationTabId));
        WorkspaceGraphContractValidation.RequireId(panelId.Value, nameof(panelId));
        SourceWindowId = sourceWindowId;
        Source = new WorkspaceInstance(source);
        ExpectedSourceRevision = expectedSourceRevision;
        SourceTabId = sourceTabId;
        DestinationWindowId = destinationWindowId;
        Destination = new WorkspaceInstance(destination);
        ExpectedDestinationRevision = expectedDestinationRevision;
        DestinationTabId = destinationTabId;
        PanelId = panelId;
    }

    public WindowInstanceId SourceWindowId { get; }
    public WorkspaceInstance Source { get; }
    public long ExpectedSourceRevision { get; }
    public TabInstanceId SourceTabId { get; }
    public WindowInstanceId DestinationWindowId { get; }
    public WorkspaceInstance Destination { get; }
    public long ExpectedDestinationRevision { get; }
    public TabInstanceId DestinationTabId { get; }
    public PanelInstanceId PanelId { get; }
}

internal static class WorkspaceGraphTransferValidation
{
    public static void ValidateCommon(
        WindowInstanceId sourceWindowId,
        WorkspaceInstance source,
        long expectedSourceRevision,
        WindowInstanceId destinationWindowId,
        WorkspaceInstance destination,
        long expectedDestinationRevision)
    {
        WorkspaceGraphContractValidation.RequireId(sourceWindowId.Value, nameof(sourceWindowId));
        WorkspaceGraphContractValidation.RequireId(destinationWindowId.Value, nameof(destinationWindowId));
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (source.Id == destination.Id)
        {
            throw new ArgumentException(
                "A cross-owner transfer requires different source and destination workspaces.",
                nameof(destination));
        }

        if (expectedSourceRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSourceRevision));
        }

        if (expectedDestinationRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedDestinationRevision));
        }
    }
}
