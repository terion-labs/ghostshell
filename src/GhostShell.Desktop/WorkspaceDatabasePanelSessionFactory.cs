using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Databases;

namespace GhostShell.Desktop;

internal sealed class WorkspaceDatabasePanelSessionFactory(
    DatabasePanelSessionFactory hostFactory) : IDatabasePanelSessionFactory
{
    private readonly WorkspaceSessionFactoryRegistry<IDatabasePanelSessionFactory> _factories = new(
        hostFactory,
        "The workspace already has a database-panel factory.");

    public CapabilitySet RelationalCapabilities => hostFactory.RelationalCapabilities;

    public CapabilitySet RedisCapabilities => hostFactory.RedisCapabilities;

    public IDisposable Register(
        WorkspaceInstanceId workspaceId,
        IDatabasePanelSessionFactory factory) =>
        _factories.Register(workspaceId, factory);

    public ValueTask<IDatabasePanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        DatabaseSessionTarget target,
        CancellationToken cancellationToken) =>
        _factories.Resolve(workspaceId).CreateAsync(
            workspaceId,
            sessionId,
            target,
            cancellationToken);
}
