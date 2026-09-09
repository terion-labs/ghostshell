namespace Asura.Application;

/// <summary>
/// A bounded host sample. CPU and working-set totals include only processes whose
/// public resource counters the operating system allowed the session host to read.
/// Network rates aggregate non-loopback interfaces that were present in two
/// consecutive network samples.
/// </summary>
public sealed record SystemStatisticsSnapshot(
    DateTimeOffset CapturedAtUtc,
    TimeSpan HostUptime,
    int LogicalProcessorCount,
    int EnumeratedProcessCount,
    int ObservedProcessCount,
    double? ObservedCpuPercent,
    long ObservedWorkingSetBytes,
    double? NetworkReceivedBytesPerSecond = null,
    double? NetworkSentBytesPerSecond = null);
