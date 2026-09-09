using System.Collections.Immutable;
using Asura.Core;

namespace Asura.Agent;

public sealed record AgentCompactionRequest(
    AgentRunId RunId,
    long Generation,
    ImmutableArray<AgentMessage> Messages,
    ImmutableArray<AgentMessage> TurnPrefixMessages = default)
{
    public bool IsSplitTurn => !TurnPrefixMessages.IsDefaultOrEmpty;
}

public sealed record AgentCompactionSettings
{
    public AgentCompactionSettings(
        bool enabled = true,
        int reserveTokens = AgentContextWindowPolicy.DefaultReserveTokens,
        int keepRecentTokens = AgentContextWindowPolicy.DefaultKeepRecentTokens)
    {
        if (reserveTokens <= 0 || keepRecentTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reserveTokens),
                "Compaction token budgets must be positive.");
        }

        Enabled = enabled;
        ReserveTokens = reserveTokens;
        KeepRecentTokens = keepRecentTokens;
    }

    public bool Enabled { get; }

    public int ReserveTokens { get; }

    public int KeepRecentTokens { get; }
}

public sealed record AgentContextUsage(long EstimatedTokens, bool UsesProviderReportedUsage);

public interface IAgentConversationCompactor
{
    ValueTask<AgentMessage> CompactAsync(
        AgentCompactionRequest request,
        CancellationToken cancellationToken);
}

public enum AgentCompactionErrorCode
{
    NothingToCompact,
    Busy,
    Cancelled,
    CompactorFailure,
    InvalidSummary,
    LimitExceeded,
    ConversationConflict,
}

public sealed record AgentCompactionResult
{
    private AgentCompactionResult(bool succeeded, AgentCompactionErrorCode? errorCode)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
    }

    public bool Succeeded { get; }

    public AgentCompactionErrorCode? ErrorCode { get; }

    internal static AgentCompactionResult Success() => new(true, null);

    internal static AgentCompactionResult Failure(AgentCompactionErrorCode errorCode) =>
        new(false, errorCode);
}
