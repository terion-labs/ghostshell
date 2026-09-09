using Asura.Core;

namespace Asura.Application;

public sealed record AgentContextRequest
{
    public const int DefaultMaximumPanelCount = WorkspaceInstance.MaximumPanelCount;
    public const int MaximumAllowedPanelCount = WorkspaceInstance.MaximumPanelCount;

    public AgentContextRequest(
        AgentTarget target,
        int maximumPanelCount = DefaultMaximumPanelCount)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (maximumPanelCount is < 1 or > MaximumAllowedPanelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPanelCount),
                maximumPanelCount,
                $"The panel limit must be between 1 and {MaximumAllowedPanelCount}.");
        }

        Target = target;
        MaximumPanelCount = maximumPanelCount;
    }

    public AgentTarget Target { get; }

    public int MaximumPanelCount { get; }
}
