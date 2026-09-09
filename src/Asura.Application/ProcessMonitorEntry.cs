namespace Asura.Application;

/// <summary>
/// Deliberately excludes command lines, environment, open files, and usernames because those
/// commonly contain credentials or other sensitive content.
/// </summary>
public sealed record ProcessMonitorEntry(
    int ProcessId,
    string Name,
    double? CpuPercent,
    long? WorkingSetBytes,
    TimeSpan? TotalProcessorTime,
    DateTimeOffset? StartedAtUtc,
    bool IsAsura);
