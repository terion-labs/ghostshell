using Asura.Core;

namespace Asura.Application;

public sealed record CloseScopeRequest(
    CloseScopeKind Scope,
    string TargetId,
    CloseDecision Decision,
    IReadOnlyDictionary<SessionId, long>? ExpectedSessionRevisions = null)
{
    public static CloseScopeRequest Panel(PanelInstanceId id, CloseDecision decision) =>
        new(CloseScopeKind.Panel, id.Value, decision);

    public static CloseScopeRequest Tab(TabInstanceId id, CloseDecision decision) =>
        new(CloseScopeKind.Tab, id.Value, decision);

    public static CloseScopeRequest Workspace(
        WorkspaceInstanceId id,
        CloseDecision decision) =>
        new(CloseScopeKind.Workspace, id.Value, decision);

    public static CloseScopeRequest Window(WindowInstanceId id, CloseDecision decision) =>
        new(CloseScopeKind.Window, id.Value, decision);

    public static CloseScopeRequest Session(SessionId id, CloseDecision decision) =>
        new(CloseScopeKind.Session, id.Value, decision);
}
