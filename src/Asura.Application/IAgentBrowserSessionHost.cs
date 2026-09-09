using Asura.Core;

namespace Asura.Application;

/// <summary>
/// The only browser execution port accepted by the governed agent path.
/// The host consumes the one-action authorization after re-resolving the
/// exact live browser target.
/// </summary>
public interface IAgentBrowserSessionHost
{
    ValueTask<HostResult<AgentBrowserActionResult>> RunAgentBrowserActionAsync(
        AgentAuthorizationId authorizationId,
        AgentBrowserAction action,
        CancellationToken cancellationToken);
}

public abstract record AgentBrowserActionResult
{
    private AgentBrowserActionResult()
    {
    }

    public sealed record Completed : AgentBrowserActionResult;

    public sealed record State(BrowserSessionState Value) : AgentBrowserActionResult;

    public sealed record Snapshot(BrowserDocumentSnapshot Value)
        : AgentBrowserActionResult;

    public sealed record Wait(BrowserWaitOutcome Value)
        : AgentBrowserActionResult;

    public sealed record Automation(BrowserAutomationReceipt Value)
        : AgentBrowserActionResult;

    public sealed record Evaluation(BrowserEvaluationResult Value)
        : AgentBrowserActionResult;
}
