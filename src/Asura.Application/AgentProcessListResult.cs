using System.Collections.ObjectModel;

namespace Asura.Application;

/// <summary>
/// A bounded, immutable, secret-minimized projection of one local process
/// monitor capture.
/// </summary>
public sealed record AgentProcessListResult
{
    public const int MaximumEntries = AgentProcessListRequest.MaximumLimit;
    public const int MaximumProjectionBytes = 64 * 1024;

    internal AgentProcessListResult(
        DateTimeOffset capturedAtUtc,
        IReadOnlyList<AgentProcessListEntry> processes,
        int enumeratedProcessCount,
        int observedProcessCount,
        bool isTruncated,
        int? matchingProcessCount = null)
    {
        if (capturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A process capture timestamp must be UTC.",
                nameof(capturedAtUtc));
        }

        ArgumentNullException.ThrowIfNull(processes);
        if (processes.Count > MaximumEntries)
        {
            throw new ArgumentException(
                "A governed process result cannot exceed 64 entries.",
                nameof(processes));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(enumeratedProcessCount);
        ArgumentOutOfRangeException.ThrowIfNegative(observedProcessCount);
        var resolvedMatchingProcessCount =
            matchingProcessCount ?? observedProcessCount;
        ArgumentOutOfRangeException.ThrowIfNegative(
            resolvedMatchingProcessCount);
        if (observedProcessCount > enumeratedProcessCount
            || resolvedMatchingProcessCount > observedProcessCount
            || processes.Count > resolvedMatchingProcessCount)
        {
            throw new ArgumentException(
                "Process result counts are inconsistent with the returned projection.");
        }

        var copies = processes
            .Select(process => process ?? throw new ArgumentException(
                "A governed process result cannot contain null entries.",
                nameof(processes)))
            .ToArray();
        if (copies
            .Select(process => process.ProcessId)
            .Distinct()
            .Count() != copies.Length)
        {
            throw new ArgumentException(
                "A governed process result cannot contain duplicate process identifiers.",
                nameof(processes));
        }

        CapturedAtUtc = capturedAtUtc;
        Processes = new ReadOnlyCollection<AgentProcessListEntry>(copies);
        EnumeratedProcessCount = enumeratedProcessCount;
        ObservedProcessCount = observedProcessCount;
        MatchingProcessCount = resolvedMatchingProcessCount;
        IsTruncated = isTruncated;
    }

    public DateTimeOffset CapturedAtUtc { get; }

    public IReadOnlyList<AgentProcessListEntry> Processes { get; }

    public int EnumeratedProcessCount { get; }

    public int ObservedProcessCount { get; }

    public int MatchingProcessCount { get; }

    public bool IsTruncated { get; }

    public int ReturnedCount => Processes.Count;

    public int RedactedNameCount =>
        Processes.Count(process => process.Name.Redacted);

    public int TruncatedNameCount =>
        Processes.Count(process => process.Name.Truncated);
}
