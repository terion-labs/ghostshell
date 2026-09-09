using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal abstract record BrowserAgentIntent
{
    private BrowserAgentIntent()
    {
    }

    public sealed record ReadState : BrowserAgentIntent;

    public sealed record Snapshot(
        bool InteractiveOnly,
        string? Filter,
        int? MaximumDepth) : BrowserAgentIntent;

    public sealed record Wait(
        BrowserWaitCondition Condition,
        TimeSpan Timeout) : BrowserAgentIntent;

    public sealed record Click(
        BrowserElementReferenceId Reference,
        long DocumentRevision) : BrowserAgentIntent;

    public sealed record Fill(
        BrowserElementReferenceId Reference,
        long DocumentRevision,
        string Text) : BrowserAgentIntent;

    public sealed record Check(
        BrowserElementReferenceId Reference,
        long DocumentRevision) : BrowserAgentIntent;

    public sealed record Mouse(
        BrowserMouseAction Action,
        double XCss,
        double YCss,
        BrowserMouseButton Button,
        BrowserMouseButtons Buttons,
        BrowserInputModifiers Modifiers,
        int ClickCount,
        double DeltaX,
        double DeltaY,
        long DocumentRevision,
        long ViewportRevision,
        long InputEpoch) : BrowserAgentIntent;

    public sealed record Key(
        BrowserKeyAction Action,
        BrowserKey KeyValue,
        BrowserInputModifiers Modifiers,
        long DocumentRevision,
        long ViewportRevision,
        long InputEpoch) : BrowserAgentIntent;

    public sealed record Scroll(
        double OriginXCss,
        double OriginYCss,
        double DeltaX,
        double DeltaY,
        BrowserInputModifiers Modifiers,
        long DocumentRevision,
        long ViewportRevision,
        long InputEpoch) : BrowserAgentIntent;

    public sealed record Evaluate(
        string Source,
        BrowserEvaluationWorld World,
        bool AwaitPromise,
        TimeSpan Timeout,
        long DocumentRevision,
        long ViewportRevision,
        long InputEpoch) : BrowserAgentIntent;

    public sealed record Navigate(BrowserAddress Address) : BrowserAgentIntent;

    public sealed record Back : BrowserAgentIntent;

    public sealed record Forward : BrowserAgentIntent;

    public sealed record Reload : BrowserAgentIntent;

    public sealed record Stop : BrowserAgentIntent;
}

internal abstract record BrowserAgentIntentResult
{
    private BrowserAgentIntentResult()
    {
    }

    public sealed record Parsed(
        BrowserAgentIntent Intent,
        PanelInstanceId? PanelId = null)
        : BrowserAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : BrowserAgentIntentResult;
}
