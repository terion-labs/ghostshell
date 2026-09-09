using Asura.Application;
using Asura.Core;
using Asura.Databases;

namespace Asura.Desktop;

internal sealed class WorkspaceDatabasePanelSessionFactory(
    DatabasePanelSessionFactory hostFactory) : IDatabasePanelSessionFactory
{
    private readonly WorkspaceSessionFactoryRegistry<IDatabasePanelSessionFactory> _factories = new(
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
