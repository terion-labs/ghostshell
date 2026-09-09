using Asura.Core;

namespace Asura.Application;

public interface IPanelSession : IAsyncDisposable
{
    SessionId Id { get; }

    PanelKind Kind { get; }

    CapabilitySet Capabilities { get; }

    ValueTask<PanelSessionSnapshot> SnapshotAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        CancellationToken cancellationToken);

    /// <summary>
    /// The panel's requests to be noticed — a bell, or an OSC 9 / OSC 777
    /// notification.
    ///
    /// Default-empty because most panel kinds have nothing to ask with, and a
    /// session that cannot be noticed should not have to say so in code.
    /// </summary>
    IAsyncEnumerable<PanelNotificationEvent> WatchNotificationsAsync(
        long afterSequence,
        CancellationToken cancellationToken) =>
        EmptyAsyncEnumerable<PanelNotificationEvent>.Instance;

    ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken);
}
