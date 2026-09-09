using Asura.Core;

namespace Asura.Application;

public interface ITerminalSessionFactory
{
    CapabilitySet Capabilities { get; }

    ValueTask<ITerminalPanelSession> CreateAsync(
        SessionId sessionId,
        TerminalLaunchRequest launch,
        CancellationToken cancellationToken);
}
