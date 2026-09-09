using Asura.Application;
using Asura.Core;
using Asura.Monitoring;

namespace Asura.Desktop;

/// <summary>
/// Selects the system-monitor implementation for a workspace while the session
/// host remains the sole owner of sessions and workspace graph links.
/// </summary>
internal sealed class WorkspaceSystemMonitorPanelSessionFactory(
    SystemMonitorPanelSessionFactory hostFactory) : ISystemMonitorPanelSessionFactory
{
    private readonly WorkspaceSessionFactoryRegistry<ISystemMonitorPanelSessionFactory> _factories = new(
        "The workspace already has a system-monitor factory.");

    public CapabilitySet StatisticsCapabilities => hostFactory.StatisticsCapabilities;

    public CapabilitySet ProcessMonitorCapabilities => hostFactory.ProcessMonitorCapabilities;

    public IDisposable Register(
        WorkspaceInstanceId workspaceId,
        ISystemMonitorPanelSessionFactory factory) => _factories.Register(workspaceId, factory);

    public ValueTask<IStatisticsPanelSession> CreateStatisticsAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        ConnectionProfile connection,
        CancellationToken cancellationToken) =>
        _factories.Resolve(workspaceId).CreateStatisticsAsync(
            workspaceId,
            sessionId,
            connection,
            cancellationToken);

    public ValueTask<IProcessMonitorPanelSession> CreateProcessMonitorAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        ConnectionProfile connection,
        CancellationToken cancellationToken) =>
        _factories.Resolve(workspaceId).CreateProcessMonitorAsync(
            workspaceId,
            sessionId,
            connection,
            cancellationToken);

}
