namespace Asura.Application;

public abstract record CloseScopeResult
{
    private CloseScopeResult()
    {
    }

    public sealed record ConfirmationRequired(
        CloseScopeKind Scope,
        string TargetId,
        IReadOnlyList<ActiveSessionSummary> Sessions) : CloseScopeResult;

    public sealed record Completed(
        CloseScopeKind Scope,
        string TargetId,
        IReadOnlyList<SessionCloseResult> Sessions) : CloseScopeResult;
}
