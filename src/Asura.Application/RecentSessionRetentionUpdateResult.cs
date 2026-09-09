namespace Asura.Application;

public sealed record RecentSessionRetentionUpdateResult
{
    public RecentSessionRetentionUpdateResult(
        StoredRecentSessionRetentionPolicy storedPolicy,
        int prunedSessionCount)
    {
        ArgumentNullException.ThrowIfNull(storedPolicy);
        if (prunedSessionCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(prunedSessionCount),
                "A pruned recent-session count cannot be negative.");
        }

        StoredPolicy = storedPolicy;
        PrunedSessionCount = prunedSessionCount;
    }

    public StoredRecentSessionRetentionPolicy StoredPolicy { get; }

    public int PrunedSessionCount { get; }
}
