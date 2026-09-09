using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Closed mutations of the single workspace already fixed by the agent run.
/// Every referenced tab or panel must come from the run's fresh graph tools.
/// </summary>
public abstract record AgentWorkspaceLayoutRequest
{
    private AgentWorkspaceLayoutRequest()
    {
    }

    public sealed record TabCreate : AgentWorkspaceLayoutRequest
    {
        public TabCreate(PanelKind kind, string? connectionRef = null)
        {
            Kind = RequireCreatableKind(kind);
            ConnectionRef = RequireConnectionRef(connectionRef);
        }

        public PanelKind Kind { get; }

        public string? ConnectionRef { get; }
    }

    public sealed record ConnectionList : AgentWorkspaceLayoutRequest;

    public sealed record PanelConnect : AgentWorkspaceLayoutRequest
    {
        public PanelConnect(PanelInstanceId panelId, string connectionRef)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionRef);
            if (connectionRef.Length > 128)
            {
                throw new ArgumentOutOfRangeException(nameof(connectionRef));
            }

            PanelId = panelId;
            ConnectionRef = string.Concat(connectionRef);
        }

        public PanelInstanceId PanelId { get; }

        public string ConnectionRef { get; }
    }

    public sealed record TabClose(TabInstanceId TabId)
        : AgentWorkspaceLayoutRequest;

    public sealed record PanelAdd : AgentWorkspaceLayoutRequest
    {
        public PanelAdd(
            TabInstanceId tabId,
            PanelKind kind,
            string? connectionRef = null)
        {
            TabId = tabId;
            Kind = RequireCreatableKind(kind);
            ConnectionRef = RequireConnectionRef(connectionRef);
        }

        public TabInstanceId TabId { get; }

        public PanelKind Kind { get; }

        public string? ConnectionRef { get; }
    }

    public sealed record PanelSplit : AgentWorkspaceLayoutRequest
    {
        public PanelSplit(
            PanelInstanceId panelId,
            AgentPanelSplitOrientation orientation,
            PanelKind kind,
            string? connectionRef = null)
        {
            if (!Enum.IsDefined(orientation))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(orientation),
                    orientation,
                    null);
            }

            PanelId = panelId;
            Orientation = orientation;
            Kind = RequireCreatableKind(kind);
            ConnectionRef = RequireConnectionRef(connectionRef);
        }

        public PanelInstanceId PanelId { get; }

        public AgentPanelSplitOrientation Orientation { get; }

        public PanelKind Kind { get; }

        public string? ConnectionRef { get; }
    }

    public sealed record PanelClose(PanelInstanceId PanelId)
        : AgentWorkspaceLayoutRequest;

    public static bool IsCreatableKind(PanelKind kind) =>
        kind is PanelKind.Terminal
            or PanelKind.Browser
            or PanelKind.FileViewer
            or PanelKind.Statistics
            or PanelKind.ProcessMonitor
            or PanelKind.Placeholder
            or PanelKind.DatabaseViewer
            or PanelKind.Docker;

    private static PanelKind RequireCreatableKind(PanelKind kind) =>
        IsCreatableKind(kind)
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, null);

    private static string? RequireConnectionRef(string? connectionRef)
    {
        if (connectionRef is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(connectionRef);
        if (connectionRef.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(connectionRef));
        }

        return string.Concat(connectionRef);
    }
}

public enum AgentPanelSplitOrientation
{
    LeftRight,
    TopBottom,
}
