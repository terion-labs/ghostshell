namespace Asura.Monitoring;

internal sealed record RawProcessObservation(
    int ProcessId,
    string Name,
    long? WorkingSetBytes,
    TimeSpan? TotalProcessorTime,
    DateTimeOffset? StartedAtUtc,
    bool IsAsura);
