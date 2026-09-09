using System.Collections.Immutable;
using Asura.Core;

namespace Asura.Application;

public sealed record AgentRunHistoryRetention
{
    public const int MaximumAllowedRuns = 256;
    public static readonly TimeSpan MaximumAllowedAge = TimeSpan.FromDays(365);

    public AgentRunHistoryRetention(
        int maximumRuns,
        TimeSpan maximumAge,
        long revision)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRuns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumRuns,
            MaximumAllowedRuns);
        if (maximumAge <= TimeSpan.Zero || maximumAge > MaximumAllowedAge)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        MaximumRuns = maximumRuns;
        MaximumAge = maximumAge;
        Revision = revision;
    }

    public int MaximumRuns { get; }

    public TimeSpan MaximumAge { get; }

    public long Revision { get; }
}

public sealed record AgentRunHistoryPolicy(
    string ProviderId,
    string ModelId,
    ImmutableArray<AgentHistoryCapabilityPermission> Permissions)
{
    public static AgentRunHistoryPolicy FromPolicy(AgentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new AgentRunHistoryPolicy(
            policy.Provider,
            policy.Model,
            [.. AgentPolicy.Capabilities.Select(capability =>
                new AgentHistoryCapabilityPermission(
                    capability,
                    policy.GetPermission(capability)))]);
    }
}

public sealed record AgentHistoryCapabilityPermission(
    AgentCapability Capability,
    AgentPermission Permission);

public sealed record AgentRunHistoryMetadata(
    AgentRunId RunId,
    AiProviderProfileId? ProviderId,
    string? ModelId,
    AgentRunHistoryPolicy BaselinePolicy,
    AgentRunHistoryPolicy RunPolicy,
    AgentRunHistoryPolicy EffectivePolicy,
    long PolicyGeneration,
    DateTimeOffset UpdatedAtUtc);

public sealed record AgentRunHistoryExportReceipt(
    int RunCount,
    DateTimeOffset ExportedAtUtc,
    long ByteCount,
    string Sha256);
