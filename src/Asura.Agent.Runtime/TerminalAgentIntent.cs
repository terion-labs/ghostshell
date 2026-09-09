using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal abstract record TerminalAgentIntent
{
    private TerminalAgentIntent()
    {
    }

    public sealed record ReadScreen : TerminalAgentIntent;

    public sealed record ReadScreenDiff(TerminalScreenDiffInput Input)
        : TerminalAgentIntent;

    public sealed record ReadScrollback(TerminalScrollbackReadInput Input)
        : TerminalAgentIntent;

    public sealed record FindScrollback(TerminalScrollbackFindInput Input)
        : TerminalAgentIntent;

    public sealed record FindOnScreen(TerminalScreenFindInput Input)
        : TerminalAgentIntent;

    public sealed record FindRenderedHistory(TerminalRenderedHistoryFindInput Input)
        : TerminalAgentIntent;

    public sealed record JumpToRenderedHistory(TerminalRenderedHistoryRowAnchor Anchor)
        : TerminalAgentIntent;

    public sealed record ScrollViewport(TerminalViewportScrollInput Input)
        : TerminalAgentIntent;

    public sealed record SendText(string Text) : TerminalAgentIntent;

    public sealed record Paste(string Text) : TerminalAgentIntent;

    public sealed record SubmitText(string Text) : TerminalAgentIntent;

    public sealed record SendKey(TerminalKeyStroke KeyStroke) : TerminalAgentIntent;

    public sealed record SendChord(TerminalCharacterChord Chord) : TerminalAgentIntent;

    public sealed record SendMouse(
        TerminalMouseInput MouseInput,
        long ExpectedContentRevision) : TerminalAgentIntent;

    public sealed record WaitForDelay(TimeSpan Delay) : TerminalAgentIntent;

    public sealed record WaitForText(string Text, TimeSpan Timeout)
        : TerminalAgentIntent;

    public sealed record WaitForChange(
        long AfterContentRevision,
        TimeSpan Timeout)
        : TerminalAgentIntent;

    public sealed record WaitForStable(
        TimeSpan StableFor,
        TimeSpan Timeout)
        : TerminalAgentIntent;

    public sealed record WaitForPromptReady(
        long AfterShellEventSequence,
        TimeSpan Timeout) : TerminalAgentIntent;

    public sealed record WaitForCommandFinished(
        long AfterShellEventSequence,
        TimeSpan Timeout) : TerminalAgentIntent;

    public sealed record Interrupt : TerminalAgentIntent;

    public sealed record Resize(int Columns, int Rows) : TerminalAgentIntent;
}

internal abstract record TerminalAgentIntentResult
{
    private TerminalAgentIntentResult()
    {
    }

    public sealed record Parsed(
        TerminalAgentIntent Intent,
        PanelInstanceId? PanelId = null)
        : TerminalAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : TerminalAgentIntentResult;
}
