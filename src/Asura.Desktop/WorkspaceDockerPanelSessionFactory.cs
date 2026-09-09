using Asura.Application;
using Asura.Core;
using Asura.Docker;

namespace Asura.Desktop;

internal sealed class WorkspaceDockerPanelSessionFactory(
    DockerPanelSessionFactory hostFactory) : IDockerPanelSessionFactory
{
    private readonly WorkspaceSessionFactoryRegistry<IDockerPanelSessionFactory> _factories = new(
        "The workspace already has a Docker-panel factory.");

    public CapabilitySet Capabilities => hostFactory.Capabilities;

    public IDisposable Register(
        WorkspaceInstanceId workspaceId,
        IDockerPanelSessionFactory factory) =>
        _factories.Register(workspaceId, factory);

    public ValueTask<IDockerPanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        DockerSessionTarget target,
        CancellationToken cancellationToken) =>
        _factories.Resolve(workspaceId).CreateAsync(
            workspaceId,
            sessionId,
            target,
            cancellationToken);
}
