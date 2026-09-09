using Asura.Application;
using Asura.Core;
using Asura.Git;

namespace Asura.Desktop;

internal sealed class WorkspaceGitPanelSessionFactory(
    GitPanelSessionFactory hostFactory) : IGitPanelSessionFactory
{
    private readonly WorkspaceSessionFactoryRegistry<IGitPanelSessionFactory> _factories = new(
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
