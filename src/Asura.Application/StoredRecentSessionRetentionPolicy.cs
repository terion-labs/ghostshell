namespace Asura.Application;

public sealed record StoredRecentSessionRetentionPolicy
{
    public StoredRecentSessionRetentionPolicy(
        RecentSessionRetentionPolicy policy,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "A stored recent-session retention revision must be positive.");
        }

        Policy = policy;
        Revision = revision;
    }

    public RecentSessionRetentionPolicy Policy { get; }

    public long Revision { get; }
}
