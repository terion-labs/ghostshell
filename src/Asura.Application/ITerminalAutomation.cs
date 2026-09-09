namespace Asura.Application;

/// <summary>
/// Provides deterministic terminal input, observation, and bounded waits.
/// </summary>
/// <remarks>
/// Exact text input and screen reads intentionally share the engine operations
/// exposed by <see cref="ITerminalProcess"/> and <see cref="ITerminalState"/>.
/// The separate views let callers request only the capability they need.
/// </remarks>
public interface ITerminalAutomation
{
    ValueTask WriteAsync(string text, CancellationToken cancellationToken);

    ValueTask SendKeyAsync(
        TerminalKeyStroke keyStroke,
        CancellationToken cancellationToken);

    /// <summary>
    /// Encodes one platform keyboard event using the terminal's negotiated
    /// keyboard protocol. Unlike <see cref="WriteAsync"/>, this preserves
    /// physical identity, repeat/release state, and consumed modifiers.
    /// </summary>
    ValueTask SendPhysicalKeyAsync(
        TerminalPhysicalKeyEvent keyEvent,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "Physical keyboard events are not supported by this terminal engine.");

    /// <summary>
    /// Sends one bounded character chord. Normal completion is the engine's
    /// irreversible delivery receipt; cancellation or failure means the chord
    /// did not cross that input boundary.
    /// </summary>
    ValueTask SendChordAsync(
        TerminalCharacterChord chord,
        CancellationToken cancellationToken);

    ValueTask EnterAsync(CancellationToken cancellationToken);

    ValueTask InterruptAsync(CancellationToken cancellationToken);

    ValueTask SendMouseAsync(
        TerminalMouseInput mouseInput,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates the content revision, grid, and mouse-tracking mode atomically
    /// with encoding one mouse event. A non-sent outcome never reaches the PTY.
    /// </summary>
    ValueTask<TerminalRevisionBoundMouseOutcome> SendMouseAtContentRevisionAsync(
        TerminalMouseInput mouseInput,
        long expectedContentRevision,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<TerminalRevisionBoundMouseOutcome>(
            new NotSupportedException(
                "Revision-bound terminal mouse input is not supported by this terminal engine."));

    ValueTask<TerminalPasteResult> PasteAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken);

    /// <summary>
    /// Pastes bounded text and submits it with one Enter key as one atomic
    /// terminal-input delivery. Engines must not implement this as two queued
    /// writes because user input could otherwise interleave between them.
    /// </summary>
    ValueTask<TerminalPasteResult> SubmitTextAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<TerminalPasteResult>(
            new NotSupportedException(
                "Atomic paste-and-submit is not supported by this terminal engine."));

    ValueTask<TerminalScreenSnapshot> ReadScreenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the current rendered screen and makes that exact snapshot eligible
    /// as the baseline for a later <see cref="ReadScreenDiffAsync"/> call.
    /// Ordinary renderer, context, and health reads use
    /// <see cref="ReadScreenAsync"/> and must not replace this baseline.
    /// </summary>
    ValueTask<TerminalScreenSnapshot> ObserveScreenAsync(
        CancellationToken cancellationToken) =>
        ReadScreenAsync(cancellationToken);

    /// <summary>
    /// Returns changed rendered rows since the latest screen revision explicitly
    /// observed through this automation instance. A missing baseline is explicit;
    /// implementations must not fabricate a diff from scrollback or raw PTY bytes.
    /// </summary>
    ValueTask<TerminalScreenDiffResult> ReadScreenDiffAsync(
        TerminalScreenDiffInput input,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<TerminalScreenDiffResult>(
            new NotSupportedException(
                "Incremental rendered-screen reads are not supported by this terminal engine."));

    ValueTask<TerminalWaitOutcome> WaitForDelayAsync(
        TerminalWaitForDelayInput input,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<TerminalWaitOutcome>(new NotSupportedException(
            "Delay/read-after waits are not supported by this terminal engine."));

    ValueTask<TerminalWaitOutcome> WaitForTextAsync(
        TerminalWaitForTextInput input,
        CancellationToken cancellationToken);

    ValueTask<TerminalWaitOutcome> WaitForChangeAsync(
        TerminalWaitForChangeInput input,
        CancellationToken cancellationToken);

    ValueTask<TerminalWaitOutcome> WaitForStableAsync(
        TerminalWaitForStableInput input,
        CancellationToken cancellationToken);

    ValueTask<TerminalWaitOutcome> WaitForPromptReadyAsync(
        TerminalWaitForPromptReadyInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TerminalWaitOutcome.Unsupported());
    }

    ValueTask<TerminalWaitOutcome> WaitForCommandFinishedAsync(
        TerminalWaitForCommandFinishedInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TerminalWaitOutcome.Unsupported());
    }
}
