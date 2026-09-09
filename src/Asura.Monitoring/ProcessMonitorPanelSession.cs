using Asura.Application;
using Asura.Core;

namespace Asura.Monitoring;

internal sealed class ProcessMonitorPanelSession : IProcessMonitorPanelSession
{
    private readonly ReadOnlyMonitorSessionLifetime _lifetime;
    private readonly ProcessResourceSampler _sampler;

    public ProcessMonitorPanelSession(
        SessionId id,
        ProcessResourceSampler sampler,
        CapabilitySet capabilities,
        TimeProvider timeProvider)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _lifetime = new ReadOnlyMonitorSessionLifetime(
            id,
            PanelKind.ProcessMonitor,
            capabilities,
            "Observing a bounded, command-line-free local process list.",
            timeProvider);
    }

    public SessionId Id => _lifetime.Id;

    public PanelKind Kind => _lifetime.Kind;

    public CapabilitySet Capabilities => _lifetime.Capabilities;

    public async ValueTask<MonitorPanelResult<ProcessMonitorSnapshot>> ListProcessesAsync(
        ProcessMonitorQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!_lifetime.IsOpen)
        {
            return Failure(MonitorPanelErrorCode.SessionClosed);
        }

        if (!query.IsValid)
        {
            return Failure(MonitorPanelErrorCode.InvalidQuery);
        }

        using var operationCancellation =
            _lifetime.CreateOperationCancellation(cancellationToken);
        var captured = await _sampler
            .CaptureAsync(
                ProcessResourceConsumer.ProcessMonitor,
                operationCancellation.Token)
            .ConfigureAwait(false);
        if (operationCancellation.IsCancellationRequested)
        {
            return Failure(MonitorPanelErrorCode.Cancelled);
        }

        if (!captured.IsSuccess)
        {
            return MonitorPanelResult<ProcessMonitorSnapshot>.Failure(captured.Error!);
        }

        var sample = captured.Value!;
        var matching = sample.Processes
            .Where(process => query.ProcessId is null
                || process.ProcessId == query.ProcessId.Value)
            .Where(process => query.NameContains is null
                || process.Name.Contains(
                    query.NameContains,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var ordered = Order(matching, query.Sort)
            .Skip(query.Offset)
            .Take(query.MaximumResults)
            .ToArray();
        return MonitorPanelResult<ProcessMonitorSnapshot>.Success(
            new ProcessMonitorSnapshot(
                sample.Statistics.CapturedAtUtc,
                Array.AsReadOnly(ordered),
                sample.EnumeratedProcessCount,
                sample.ObservedProcessCount,
                sample.SourceWasTruncated
                    || matching.Length != ordered.Length,
                matching.Length));
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

    private static IEnumerable<ProcessMonitorEntry> Order(
        IEnumerable<ProcessMonitorEntry> processes,
        ProcessMonitorSort sort) =>
        sort switch
        {
            ProcessMonitorSort.CpuDescending => processes
                .OrderByDescending(process => process.CpuPercent.HasValue)
                .ThenByDescending(process => process.CpuPercent)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId),
            ProcessMonitorSort.CpuAscending => processes
                .OrderByDescending(process => process.CpuPercent.HasValue)
                .ThenBy(process => process.CpuPercent)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId),
            ProcessMonitorSort.MemoryDescending => processes
                .OrderByDescending(process => process.WorkingSetBytes.HasValue)
                .ThenByDescending(process => process.WorkingSetBytes)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId),
            ProcessMonitorSort.MemoryAscending => processes
                .OrderByDescending(process => process.WorkingSetBytes.HasValue)
                .ThenBy(process => process.WorkingSetBytes)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId),
            ProcessMonitorSort.NameAscending => processes
                .OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId),
            ProcessMonitorSort.NameDescending => processes
                .OrderByDescending(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId),
            ProcessMonitorSort.ProcessIdAscending => processes
                .OrderBy(process => process.ProcessId),
            ProcessMonitorSort.ProcessIdDescending => processes
                .OrderByDescending(process => process.ProcessId),
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null),
        };

    private static MonitorPanelResult<ProcessMonitorSnapshot> Failure(
        MonitorPanelErrorCode code) =>
        MonitorPanelResult<ProcessMonitorSnapshot>.Failure(MonitorPanelError.Create(code));
}
