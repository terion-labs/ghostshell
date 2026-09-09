namespace Asura.Core;

/// <summary>
/// Shared context-budget defaults used by automatic conversation compaction and
/// its presentation. Values follow the PI agent defaults.
/// </summary>
public static class AgentContextWindowPolicy
{
    public const int DefaultReserveTokens = 16 * 1024;

    public const int DefaultKeepRecentTokens = 20_000;

    public static int EffectiveLimit(int contextWindowTokens) =>
        Math.Max(1, contextWindowTokens - DefaultReserveTokens);
}
