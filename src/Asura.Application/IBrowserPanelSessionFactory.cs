using Asura.Core;

namespace Asura.Application;

public interface IBrowserPanelSessionFactory
{
    CapabilitySet Capabilities { get; }

    ValueTask<IBrowserPanelSession> CreateAsync(
        SessionId sessionId,
        BrowserAddress initialAddress,
        CancellationToken cancellationToken);
}
