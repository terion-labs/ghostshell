using Asura.Application;
using Asura.Core;

namespace Asura.Monitoring;

internal sealed class StatisticsPanelSession : IStatisticsPanelSession
{
    private readonly ReadOnlyMonitorSessionLifetime _lifetime;
    private readonly ProcessResourceSampler _sampler;

    public StatisticsPanelSession(
        SessionId id,
        ProcessResourceSampler sampler,
        CapabilitySet capabilities,
        TimeProvider timeProvider)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _lifetime = new ReadOnlyMonitorSessionLifetime(
            id,
            PanelKind.Statistics,
            capabilities,
            "Observing bounded local-host resource counters.",
            timeProvider);
    }

    public SessionId Id => _lifetime.Id;

    public PanelKind Kind => _lifetime.Kind;

    public CapabilitySet Capabilities => _lifetime.Capabilities;

    public async ValueTask<MonitorPanelResult<SystemStatisticsSnapshot>> ReadStatisticsAsync(
        CancellationToken cancellationToken)
    {
        if (!_lifetime.IsOpen)
        {
            return Closed<SystemStatisticsSnapshot>();
        }

        using var operationCancellation =
            _lifetime.CreateOperationCancellation(cancellationToken);
        var captured = await _sampler
            .CaptureAsync(
                ProcessResourceConsumer.Statistics,
                operationCancellation.Token)
            .ConfigureAwait(false);
        if (operationCancellation.IsCancellationRequested)
        {
            return MonitorPanelResult<SystemStatisticsSnapshot>.Failure(
                MonitorPanelError.Create(MonitorPanelErrorCode.Cancelled));
        }

        return captured.IsSuccess
            ? MonitorPanelResult<SystemStatisticsSnapshot>.Success(
                captured.Value!.Statistics)
            : MonitorPanelResult<SystemStatisticsSnapshot>.Failure(captured.Error!);
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
        _lifetime.SnapshotAsync(cancellationToken);

    public IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        CancellationToken cancellationToken) =>
        _lifetime.WatchAsync(afterSequence, cancellationToken);

    public ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken) =>
        _lifetime.CloseAsync(mode, cancellationToken);

    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();

    private static MonitorPanelResult<T> Closed<T>() =>
        MonitorPanelResult<T>.Failure(
            MonitorPanelError.Create(MonitorPanelErrorCode.SessionClosed));
}
