using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Resolves the live network boundary for consumers whose lifetime is owned by
/// the application rather than by an individual workspace runtime service.
/// </summary>
public interface IWorkspaceNetworkRouteResolver
{
    IWorkspaceNetworkConnector? ConnectorFor(WorkspaceInstanceId workspaceId);

    IConnectionCommandRuntime? IsolatedCommandRuntimeFor(
        WorkspaceInstanceId workspaceId);
}
