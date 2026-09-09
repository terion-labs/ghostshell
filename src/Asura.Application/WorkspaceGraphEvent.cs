using Asura.Core;

namespace Asura.Application;

public enum WorkspaceGraphEventKind
{
    Registered,
    Replaced,
    TabActivated,
    PanelActivated,
    Removed,
    PanelSessionLinked,
    PanelSessionUnlinked,
    TransferCommitted,
}

public sealed record WorkspaceGraphEvent
{
    public WorkspaceGraphEvent(
        WindowInstanceId windowId,
        WorkspaceInstance workspace,
        long sequence,
        long revision,
        WorkspaceGraphEventKind kind,
        DateTimeOffset timestampUtc,
        TabInstanceId? tabId = null,
        PanelInstanceId? panelId = null,
        SessionId? sessionId = null)
    {
        WorkspaceGraphContractValidation.RequireId(windowId.Value, nameof(windowId));

        ArgumentNullException.ThrowIfNull(workspace);
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (sessionId is { } affectedSessionId)
        {
            WorkspaceGraphContractValidation.RequireId(
                affectedSessionId.Value,
                nameof(sessionId));
        }

        var isPanelSessionEvent = kind is
            WorkspaceGraphEventKind.PanelSessionLinked or
            WorkspaceGraphEventKind.PanelSessionUnlinked;
        if (isPanelSessionEvent
            && (tabId is null || panelId is null || sessionId is null))
        {
            throw new ArgumentException(
                "A panel-session event requires tab, panel, and session identities.",
                nameof(kind));
        }

        if (!isPanelSessionEvent && sessionId is not null)
        {
            throw new ArgumentException(
                "Only panel-session events can identify an affected session.",
                nameof(sessionId));
        }

        WindowId = windowId;
        Workspace = new WorkspaceInstance(workspace);
        Sequence = sequence;
        Revision = revision;
        Kind = kind;
        TimestampUtc = timestampUtc.ToUniversalTime();
        TabId = tabId;
        PanelId = panelId;
        SessionId = sessionId;
    }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstance Workspace { get; }

    public WorkspaceInstanceId WorkspaceId => Workspace.Id;

    public long Sequence { get; }

    public long Revision { get; }

    public WorkspaceGraphEventKind Kind { get; }

    public int PayloadVersion => 1;

    public DateTimeOffset TimestampUtc { get; }

    public TabInstanceId? TabId { get; }

    public PanelInstanceId? PanelId { get; }

    /// <summary>
    /// Identifies the session added to or removed from <see cref="PanelId"/> for session-link events.
    /// </summary>
    public SessionId? SessionId { get; }
}
