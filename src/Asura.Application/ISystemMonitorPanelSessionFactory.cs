using Asura.Core;

namespace Asura.Application;

public interface ISystemMonitorPanelSessionFactory
{
    CapabilitySet StatisticsCapabilities { get; }

    CapabilitySet ProcessMonitorCapabilities { get; }

    ValueTask<IStatisticsPanelSession> CreateStatisticsAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        ConnectionProfile connection,
        CancellationToken cancellationToken);

    ValueTask<IProcessMonitorPanelSession> CreateProcessMonitorAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        ConnectionProfile connection,
        CancellationToken cancellationToken);
}
