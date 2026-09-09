namespace Asura.Application;

/// <summary>
/// Exposes bounded metadata and deletion operations for recovery snapshots that
/// do not belong to the active application run. Snapshot payloads never cross
/// this boundary.
/// </summary>
public interface IRuntimeRecoveryDataControl
{
    ValueTask<ApplicationRunResult<RuntimeRecoveryInventory>> ListAsync(
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<long>> DiscardRunAsync(
        string runId,
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<long>> DiscardAllAsync(
        CancellationToken cancellationToken);
}

public sealed record RuntimeRecoveryRunSummary
{
    public RuntimeRecoveryRunSummary(
        string runId,
        long snapshotCount,
        long payloadBytes,
        DateTimeOffset lastUpdatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId.Length > 256 || runId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The recovery run identifier is outside the supported bounds.",
                nameof(runId));
        }

        if (snapshotCount is <= 0 or > RuntimeRecoveryInventory.MaximumSnapshotsPerRun)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshotCount),
                $"A recovery run summary must contain between 1 and "
                + $"{RuntimeRecoveryInventory.MaximumSnapshotsPerRun} snapshots.");
        }

        if (payloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadBytes),
                "Recovery payload bytes cannot be negative.");
        }

        RunId = runId;
        SnapshotCount = snapshotCount;
        PayloadBytes = payloadBytes;
        LastUpdatedAt = lastUpdatedAt;
    }

    public string RunId { get; }

    public long SnapshotCount { get; }

    public long PayloadBytes { get; }

    public DateTimeOffset LastUpdatedAt { get; }
}

public sealed record RuntimeRecoveryInventory
{
    public const int MaximumListedRuns = 100;
    public const int MaximumSnapshotsPerRun = 32;

    public RuntimeRecoveryInventory(
        IReadOnlyList<RuntimeRecoveryRunSummary> runs,
        bool hasAdditionalRuns)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count > MaximumListedRuns)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runs),
                $"At most {MaximumListedRuns} recovery runs may be returned.");
        }

        if (hasAdditionalRuns && runs.Count != MaximumListedRuns)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hasAdditionalRuns),
                "A truncated inventory must contain one complete page of recovery runs.");
        }

        if (runs.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count()
            != runs.Count)
        {
            throw new ArgumentException(
                "Recovery inventory run identifiers must be unique.",
                nameof(runs));
        }

        Runs = [.. runs];
        HasAdditionalRuns = hasAdditionalRuns;
        ListedSnapshotCount = checked(Runs.Sum(item => item.SnapshotCount));
        ListedPayloadBytes = checked(Runs.Sum(item => item.PayloadBytes));
    }

    public IReadOnlyList<RuntimeRecoveryRunSummary> Runs { get; }

    public int ListedRunCount => Runs.Count;

    public long ListedSnapshotCount { get; }

    public long ListedPayloadBytes { get; }

    public bool HasAdditionalRuns { get; }

    public bool IsTruncated => HasAdditionalRuns;
}
