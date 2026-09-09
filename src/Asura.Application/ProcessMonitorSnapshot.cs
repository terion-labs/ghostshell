namespace Asura.Application;

public sealed record ProcessMonitorSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ProcessMonitorEntry> Processes,
    int EnumeratedProcessCount,
    int ObservedProcessCount,
    bool IsTruncated,
    int? MatchingProcessCount = null);
