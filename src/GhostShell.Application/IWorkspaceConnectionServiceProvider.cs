namespace GhostShell.Application;

/// <summary>A mount-free connection relay, not an interactive workspace provider.</summary>
public interface IWorkspaceConnectionServiceProvider : IWorkspaceIsolationProvider
{
    ValueTask ConfigureServiceNetworkingAsync(WorkspaceIsolationBinding binding, CancellationToken cancellationToken);
}
