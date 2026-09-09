namespace Asura.Application;

/// <summary>
/// Closed read-only workspace graph operations. Paging is fixed by the
/// application contract; provider input can select only a non-negative offset.
/// </summary>
public abstract record AgentWorkspaceGraphRequest
{
    public const int PageSize = 16;
    public const int MaximumOffset = 48;

    private AgentWorkspaceGraphRequest()
    {
    }

    public sealed record WorkspaceInspect : AgentWorkspaceGraphRequest;

    public sealed record TabList : AgentWorkspaceGraphRequest
    {
        public TabList(int offset = 0)
        {
            ValidateOffset(offset);
            Offset = offset;
        }

        public int Offset { get; }
    }

    public sealed record PanelList : AgentWorkspaceGraphRequest
    {
        public PanelList(int offset = 0)
        {
            ValidateOffset(offset);
            Offset = offset;
        }

        public int Offset { get; }
    }

    private static void ValidateOffset(int offset)
    {
        if (offset < 0
            || offset > MaximumOffset
            || offset % PageSize != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                $"A graph page offset must be one of 0, 16, 32, or {MaximumOffset}.");
        }
    }
}
