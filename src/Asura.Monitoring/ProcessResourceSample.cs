using Asura.Application;

namespace Asura.Monitoring;

internal sealed record ProcessResourceSample(
    SystemStatisticsSnapshot Statistics,
    IReadOnlyList<ProcessMonitorEntry> Processes,
    int EnumeratedProcessCount,
    int ObservedProcessCount,
    bool SourceWasTruncated);
