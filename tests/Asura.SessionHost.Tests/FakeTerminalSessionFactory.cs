using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

internal sealed class FakeTerminalSessionFactory : ITerminalSessionFactory
{
    private readonly Dictionary<SessionId, FakeTerminalSession> _sessions = [];

    public CapabilitySet Capabilities { get; } = new(
    [
        SessionCapabilities.NativeRenderer,
        SessionCapabilities.TerminalAgentInputBarrier,
        SessionCapabilities.TerminalReadScreen,
        SessionCapabilities.TerminalWrite,
        SessionCapabilities.TerminalSendKeys,
        SessionCapabilities.TerminalSendChord,
        SessionCapabilities.TerminalEnter,
        SessionCapabilities.TerminalInterrupt,
        SessionCapabilities.TerminalWait,
        SessionCapabilities.TerminalMouse,
        SessionCapabilities.TerminalRevisionBoundMouse,
        SessionCapabilities.TerminalScrollback,
        SessionCapabilities.TerminalScrollbackRead,
        SessionCapabilities.TerminalScrollbackFind,
        SessionCapabilities.TerminalRenderedHistory,
        SessionCapabilities.TerminalClearScrollback,
        SessionCapabilities.TerminalFind,
        SessionCapabilities.TerminalSelection,
        SessionCapabilities.TerminalPaste,
        SessionCapabilities.TerminalResize,
        SessionCapabilities.TerminalFocus,
    ]);

    public int CreateCount { get; private set; }

    public bool NewSessionsHaveActiveWork { get; set; }

    public PanelCloseOutcome? CloseOutcomeOverride { get; set; }

    public bool ThrowWhenClosing { get; set; }

    public bool BlockCreation { get; set; }

    public Func<FakeTerminalSession, CancellationToken, ValueTask>? AfterCreateAsync
    {
        get;
        set;
    }

    public Func<CancellationToken, ValueTask>? BeforeSnapshotForNewSessions
    {
        get;
        set;
    }

    public string? ExcludedCapabilityForNewSessions { get; set; }

    public TaskCompletionSource CreationStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource AllowCreation { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeTerminalSession this[SessionId id] => _sessions[id];

    public async ValueTask<ITerminalPanelSession> CreateAsync(
        SessionId sessionId,
        TerminalLaunchRequest launch,
        CancellationToken cancellationToken)
    {
        _ = launch;
        cancellationToken.ThrowIfCancellationRequested();
        if (BlockCreation)
        {
            CreationStarted.TrySetResult();
            await AllowCreation.Task.WaitAsync(cancellationToken);
        }

        CreateCount++;
        var session = new FakeTerminalSession(
            sessionId,
            launch,
            NewSessionsHaveActiveWork,
            CloseOutcomeOverride,
            ThrowWhenClosing)
        {
            BeforeSnapshotAsync = BeforeSnapshotForNewSessions,
        };
        if (ExcludedCapabilityForNewSessions is { } excludedCapability)
        {
            session.RemoveCapability(excludedCapability);
        }

        _sessions.Add(sessionId, session);
        if (AfterCreateAsync is { } afterCreate)
        {
            await afterCreate(session, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }
}
