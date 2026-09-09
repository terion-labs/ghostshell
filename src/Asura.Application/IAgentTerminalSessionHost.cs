using Asura.Core;

namespace Asura.Application;

/// <summary>
/// The only terminal execution port accepted by the governed agent path.
/// The host consumes the one-action authorization itself after re-resolving
/// the exact live target.
/// </summary>
public interface IAgentTerminalSessionHost
{
    ValueTask<HostResult<AgentTerminalActionResult>> RunAgentTerminalActionAsync(
        AgentAuthorizationId authorizationId,
        AgentTerminalAction action,
        CancellationToken cancellationToken);
}

public abstract record AgentTerminalActionResult
{
    private AgentTerminalActionResult()
    {
    }

    public sealed record Completed : AgentTerminalActionResult;

    public sealed record Screen(TerminalScreenSnapshot Snapshot)
        : AgentTerminalActionResult;

    public sealed record ScreenDiff(TerminalScreenDiffResult Result)
        : AgentTerminalActionResult;

    public sealed record Scrollback(TerminalScrollbackSnapshot Snapshot)
        : AgentTerminalActionResult;

    public sealed record Find(TerminalScrollbackFindResult Result)
        : AgentTerminalActionResult;

    public sealed record ScreenFind(TerminalScreenFindResult Result)
        : AgentTerminalActionResult;

    public sealed record RenderedHistoryFind(TerminalRenderedHistoryFindResult Result)
        : AgentTerminalActionResult;

    public sealed record Wait(TerminalWaitOutcome Outcome)
        : AgentTerminalActionResult;
}
