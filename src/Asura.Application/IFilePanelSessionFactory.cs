using Asura.Core;

namespace Asura.Application;

public interface IFilePanelSessionFactory
{
    CapabilitySet Capabilities { get; }

    ValueTask<IFilePanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        FilePanelLocation initialLocation,
        CancellationToken cancellationToken);
}
