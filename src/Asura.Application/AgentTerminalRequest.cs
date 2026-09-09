using Asura.Core;

namespace Asura.Application;

/// <summary>
/// The closed set of terminal operations that can be prepared for agent authorization.
/// Provider-defined names and argument bags cannot extend this set. Mutation requests
/// carry execution material only; the trusted host acquires their one-action input lease.
/// </summary>
public abstract record AgentTerminalRequest
{
    private AgentTerminalRequest()
    {
    }

    public sealed record ReadScreen(SessionId SessionId) : AgentTerminalRequest;

    public sealed record ReadScreenDiff(
        SessionId SessionId,
        TerminalScreenDiffInput Input) : AgentTerminalRequest;

    public sealed record ReadScrollback(
        SessionId SessionId,
        TerminalScrollbackReadInput Input) : AgentTerminalRequest;

    public sealed record FindScrollback(
        SessionId SessionId,
        TerminalScrollbackFindInput Input) : AgentTerminalRequest;

    public sealed record FindOnScreen(
        SessionId SessionId,
        TerminalScreenFindInput Input) : AgentTerminalRequest;

    public sealed record FindRenderedHistory(
        SessionId SessionId,
        TerminalRenderedHistoryFindInput Input) : AgentTerminalRequest;

    public sealed record JumpToRenderedHistory(
        SessionId SessionId,
        TerminalRenderedHistoryRowAnchor Anchor) : AgentTerminalRequest;

    public sealed record ScrollViewport(
        SessionId SessionId,
        TerminalViewportScrollInput Input) : AgentTerminalRequest;

    public sealed record SendText(
        SessionId SessionId,
        string Text) : AgentTerminalRequest;

    public sealed record Paste(
        SessionId SessionId,
        string Text) : AgentTerminalRequest;

    public sealed record SubmitText(
        SessionId SessionId,
        string Text) : AgentTerminalRequest;

    public sealed record SendKey(
        SessionId SessionId,
        TerminalKeyStroke KeyStroke) : AgentTerminalRequest;

    public sealed record SendChord(
        SessionId SessionId,
        TerminalCharacterChord Chord) : AgentTerminalRequest;

    public sealed record SendMouse(
        SessionId SessionId,
        TerminalMouseInput MouseInput,
        long ExpectedContentRevision) : AgentTerminalRequest;

    public sealed record WaitForDelay(TerminalWaitForDelayRequest Value) : AgentTerminalRequest;

    public sealed record WaitForText(TerminalWaitForTextRequest Value) : AgentTerminalRequest;

    public sealed record WaitForChange(TerminalWaitForChangeRequest Value) : AgentTerminalRequest;

    public sealed record WaitForStable(TerminalWaitForStableRequest Value) : AgentTerminalRequest;

    public sealed record WaitForPromptReady(TerminalWaitForPromptReadyRequest Value)
        : AgentTerminalRequest;

    public sealed record WaitForCommandFinished(
        TerminalWaitForCommandFinishedRequest Value) : AgentTerminalRequest;

    public sealed record Interrupt(SessionId SessionId) : AgentTerminalRequest;

    public sealed record Resize(TerminalResizeRequest Value) : AgentTerminalRequest;
}
