namespace Asura.Application;

/// <summary>
/// A validated numeric-only projection of one local Statistics capture.
/// </summary>
public sealed record AgentStatisticsReadResult
{
    internal AgentStatisticsReadResult(
        DateTimeOffset capturedAtUtc,
        TimeSpan hostUptime,
        int logicalProcessorCount,
        int enumeratedProcessCount,
        int observedProcessCount,
        double? observedCpuPercent,
        long observedWorkingSetBytes,
        double? networkReceivedBytesPerSecond,
        double? networkSentBytesPerSecond)
    {
        Validate(
            capturedAtUtc,
            hostUptime,
            logicalProcessorCount,
            enumeratedProcessCount,
            observedProcessCount,
            observedCpuPercent,
            observedWorkingSetBytes,
            networkReceivedBytesPerSecond,
            networkSentBytesPerSecond);

        CapturedAtUtc = capturedAtUtc;
        HostUptime = hostUptime;
        LogicalProcessorCount = logicalProcessorCount;
        EnumeratedProcessCount = enumeratedProcessCount;
        ObservedProcessCount = observedProcessCount;
        ObservedCpuPercent = observedCpuPercent;
        ObservedWorkingSetBytes = observedWorkingSetBytes;
        NetworkReceivedBytesPerSecond = networkReceivedBytesPerSecond;
        NetworkSentBytesPerSecond = networkSentBytesPerSecond;
    }

    public DateTimeOffset CapturedAtUtc { get; }

    public TimeSpan HostUptime { get; }

    public int LogicalProcessorCount { get; }

    public int EnumeratedProcessCount { get; }

    public int ObservedProcessCount { get; }

    public double? ObservedCpuPercent { get; }

    public long ObservedWorkingSetBytes { get; }

    public double? NetworkReceivedBytesPerSecond { get; }

    public double? NetworkSentBytesPerSecond { get; }

    private static void Validate(
        DateTimeOffset capturedAtUtc,
        TimeSpan hostUptime,
        int logicalProcessorCount,
        int enumeratedProcessCount,
        int observedProcessCount,
        double? observedCpuPercent,
        long observedWorkingSetBytes,
        double? networkReceivedBytesPerSecond,
        double? networkSentBytesPerSecond)
    {
        if (capturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A statistics capture timestamp must be UTC.",
                nameof(capturedAtUtc));
        }

        if (hostUptime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(hostUptime));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(logicalProcessorCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(enumeratedProcessCount);
        ArgumentOutOfRangeException.ThrowIfNegative(observedProcessCount);
        ArgumentOutOfRangeException.ThrowIfNegative(observedWorkingSetBytes);
        if (observedProcessCount > enumeratedProcessCount)
        {
            throw new ArgumentException(
                "Statistics process counts are inconsistent.");
        }

        RequirePercentage(observedCpuPercent, nameof(observedCpuPercent));
        RequireRate(
            networkReceivedBytesPerSecond,
            nameof(networkReceivedBytesPerSecond));
        RequireRate(
            networkSentBytesPerSecond,
            nameof(networkSentBytesPerSecond));
    }

    private static void RequirePercentage(double? value, string parameterName)
    {
        if (value is { } number
            && (!double.IsFinite(number) || number is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void RequireRate(double? value, string parameterName)
    {
        if (value is { } number
            && (!double.IsFinite(number) || number < 0))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
