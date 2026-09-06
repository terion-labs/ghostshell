using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Git;

namespace GhostShell.Desktop;

internal sealed class WorkspaceGitPanelSessionFactory(
    GitPanelSessionFactory hostFactory) : IGitPanelSessionFactory
{
    private readonly WorkspaceSessionFactoryRegistry<IGitPanelSessionFactory> _factories = new(
        hostFactory,
        "The workspace already has a Git-panel factory.");

    public CapabilitySet Capabilities => hostFactory.Capabilities;

    public IDisposable Register(
        WorkspaceInstanceId workspaceId,
        IGitPanelSessionFactory factory) =>
        _factories.Register(workspaceId, factory);

    public ValueTask<IGitPanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        GitSessionTarget target,
        CancellationToken cancellationToken) =>
        _factories.Resolve(workspaceId).CreateAsync(
            workspaceId,
            sessionId,
            target,
            cancellationToken);
}
