using Asura.Core;

namespace Asura.Application;

/// <summary>
/// A bounded descriptive projection of one exact panel/session owner. It is not reusable
/// authorization and must be resolved again before an agent operation executes.
/// </summary>
public sealed partial record AgentContextPanel
{
    private AgentContextPanel(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId,
        string? workspaceTitle,
        long workspaceRevision,
        long graphSequence,
        int? graphTabOrder,
        int? graphPanelOrder,
        TabInstanceId tabId,
        string? tabTitle,
        PanelInstanceId panelId,
        string? panelTitle,
        PanelKind kind,
        bool hasRegisteredGraph,
        bool isCurrentPanelSession,
        bool isVisible,
        bool isFocused,
        SessionDescriptor? session)
    {
        ValidateIdentifier(windowId.Value, nameof(windowId));
        ValidateIdentifier(workspaceId.Value, nameof(workspaceId));
        ValidateIdentifier(tabId.Value, nameof(tabId));
        ValidateIdentifier(panelId.Value, nameof(panelId));
        if (workspaceRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workspaceRevision));
        }

        if (graphSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(graphSequence));
        }

        if (hasRegisteredGraph
            && (graphTabOrder is null || graphPanelOrder is null))
        {
            throw new ArgumentException(
                "A registered graph panel requires its structural order.");
        }

        if (!hasRegisteredGraph
            && (graphSequence != 0
                || graphTabOrder is not null
                || graphPanelOrder is not null))
        {
            throw new ArgumentException(
                "An unregistered panel cannot carry graph sequence or order.");
        }

        if (graphTabOrder < 0 || graphPanelOrder < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(graphTabOrder),
                "Graph order values cannot be negative.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ValidateSessionOwner(
            windowId,
            workspaceId,
            tabId,
            panelId,
            kind,
            session);
        WindowId = windowId;
        WorkspaceId = workspaceId;
        WorkspaceTitle = CopyTitle(workspaceTitle, nameof(workspaceTitle));
        WorkspaceRevision = workspaceRevision;
        GraphSequence = graphSequence;
        GraphTabOrder = graphTabOrder;
        GraphPanelOrder = graphPanelOrder;
        TabId = tabId;
        TabTitle = CopyTitle(tabTitle, nameof(tabTitle));
        PanelId = panelId;
        PanelTitle = CopyTitle(panelTitle, nameof(panelTitle));
        Kind = kind;
        HasRegisteredGraph = hasRegisteredGraph;
        IsCurrentPanelSession = isCurrentPanelSession;
        IsVisible = isVisible;
        IsFocused = isFocused;
        SessionId = session?.Id;
        Lifecycle = session?.Lifecycle;
        Health = session?.Health;
        SessionRevision = session?.Revision;
        HasActiveWork = session?.HasActiveWork ?? false;
        Capabilities = CopyCapabilities(session?.Capabilities);
        ConnectionId = session?.TerminalMetadata?.ConnectionId;
        ConnectionBoundary = CopyConnectionBoundary(session?.TerminalMetadata);
        InitialWorkingDirectory = CopyWorkingDirectory(
            session?.TerminalMetadata?.InitialWorkingDirectory,
            nameof(InitialWorkingDirectory));
        CurrentWorkingDirectory = CopyWorkingDirectory(
            session?.TerminalMetadata?.CurrentWorkingDirectory,
            nameof(CurrentWorkingDirectory));
        FileMetadata = session?.FileMetadata;
        BrowserMetadata = session?.BrowserMetadata;
        GitMetadata = session?.GitMetadata;
    }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstanceId WorkspaceId { get; }

    public string? WorkspaceTitle { get; }

    public long WorkspaceRevision { get; }

    public long GraphSequence { get; }

    /// <summary>
    /// Zero-based tab position in the registered graph. This is structural
    /// comparison evidence and is not a user-facing ordinal.
    /// </summary>
    public int? GraphTabOrder { get; }

    /// <summary>
    /// Zero-based panel position within its registered graph tab.
    /// </summary>
    public int? GraphPanelOrder { get; }

    public TabInstanceId TabId { get; }

    public string? TabTitle { get; }

    public PanelInstanceId PanelId { get; }

    public string? PanelTitle { get; }

    public PanelKind Kind { get; }

    public bool HasRegisteredGraph { get; }

    public bool IsCurrentPanelSession { get; }

    public bool IsVisible { get; }

    public bool IsFocused { get; }

    public SessionId? SessionId { get; }

    public SessionLifecycle? Lifecycle { get; }

    public SessionHealth? Health { get; }

    public long? SessionRevision { get; }

    public bool HasActiveWork { get; }

    public IReadOnlyList<string> Capabilities { get; }

    public ConnectionId? ConnectionId { get; }

    public string? ConnectionBoundary { get; }

    public string? InitialWorkingDirectory { get; }

    public string? CurrentWorkingDirectory { get; }

    public FileSessionMetadata? FileMetadata { get; }

    public BrowserSessionMetadata? BrowserMetadata { get; }

    public GitSessionMetadata? GitMetadata { get; }

    public static AgentContextPanel ForGraphPanel(
        WorkspaceGraphSnapshot graph,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        SessionDescriptor? session)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var tab = graph.Workspace.Tabs.SingleOrDefault(candidate => candidate.Id == tabId)
            ?? throw new ArgumentException(
                "The context tab must belong to the supplied workspace graph.",
                nameof(tabId));
        var panel = tab.Panels.SingleOrDefault(candidate => candidate.Id == panelId)
            ?? throw new ArgumentException(
                "The context panel must belong to the supplied tab.",
                nameof(panelId));
        if (panel.SessionId != session?.Id)
        {
            throw new ArgumentException(
                "A graph panel requires its exact current live session metadata.",
                nameof(session));
        }

        return FromGraph(graph, tab, panel, session, isCurrentPanelSession: true);
    }

    public static AgentContextPanel ForExactSession(
        SessionDescriptor session,
        WorkspaceGraphSnapshot? graph = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (graph is null)
        {
            var owner = session.Owner;
            return new AgentContextPanel(
                owner.WindowId,
                owner.WorkspaceId,
                null,
                0,
                0,
                null,
                null,
                owner.TabId,
                null,
                owner.PanelId,
                null,
                session.Kind,
                hasRegisteredGraph: false,
                isCurrentPanelSession: false,
                isVisible: false,
                isFocused: false,
                session);
        }

        var exactOwner = session.Owner;
        if (graph.WindowId != exactOwner.WindowId
            || graph.Workspace.Id != exactOwner.WorkspaceId)
        {
            throw new ArgumentException(
                "The graph does not own the exact session workspace.",
                nameof(graph));
        }

        var tab = graph.Workspace.Tabs.SingleOrDefault(
            candidate => candidate.Id == exactOwner.TabId)
            ?? throw new ArgumentException(
                "The exact session owner tab is stale.",
                nameof(graph));
        var panel = tab.Panels.SingleOrDefault(
            candidate => candidate.Id == exactOwner.PanelId)
            ?? throw new ArgumentException(
                "The exact session owner panel is stale.",
                nameof(graph));
        return FromGraph(
            graph,
            tab,
            panel,
            session,
            panel.SessionId == session.Id);
    }

    private static AgentContextPanel FromGraph(
        WorkspaceGraphSnapshot graph,
        TabInstance tab,
        PanelInstance panel,
        SessionDescriptor? session,
        bool isCurrentPanelSession)
    {
        var graphTabOrder = -1;
        for (var index = 0; index < graph.Workspace.Tabs.Count; index++)
        {
            if (graph.Workspace.Tabs[index].Id == tab.Id)
            {
                graphTabOrder = index;
                break;
            }
        }

        var graphPanelOrder = -1;
        for (var index = 0; index < tab.Panels.Count; index++)
        {
            if (tab.Panels[index].Id == panel.Id)
            {
                graphPanelOrder = index;
                break;
            }
        }

        if (graphTabOrder < 0 || graphPanelOrder < 0)
        {
            throw new ArgumentException(
                "A context panel must belong to the supplied graph structure.",
                nameof(panel));
        }

        return new AgentContextPanel(
            graph.WindowId,
            graph.Workspace.Id,
            graph.Workspace.Title,
            graph.Revision,
            graph.LastSequence,
            graphTabOrder,
            graphPanelOrder,
            tab.Id,
            tab.Title,
            panel.Id,
            panel.Title,
            panel.Kind,
            hasRegisteredGraph: true,
            isCurrentPanelSession,
            graph.Workspace.ActiveTabId == tab.Id,
            graph.Workspace.ActiveTabId == tab.Id && tab.ActivePanelId == panel.Id,
            session);
    }
}
