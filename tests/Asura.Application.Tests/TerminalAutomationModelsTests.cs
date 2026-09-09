namespace Asura.Application.Tests;

public sealed class TerminalAutomationModelsTests
{
    [Fact]
    public void Agent_input_barrier_has_a_stable_explicit_capability_name()
    {
        Assert.Equal(
            "terminal.agent_input_barrier",
            SessionCapabilities.TerminalAgentInputBarrier);
    }

    [Fact]
    public void Character_chord_has_a_stable_distinct_capability_name()
    {
        Assert.Equal(
            "terminal.send_chord",
            SessionCapabilities.TerminalSendChord);
        Assert.NotEqual(
            SessionCapabilities.TerminalSendKeys,
            SessionCapabilities.TerminalSendChord, StringComparer.Ordinal);
    }

    [Fact]
    public void Enter_interrupt_and_wait_have_distinct_stable_operation_and_capability_names()
    {
        Assert.Equal("terminal.enter", ApplicationOperations.TerminalEnter);
        Assert.Equal("terminal.interrupt", ApplicationOperations.TerminalInterrupt);
        Assert.Equal("terminal.wait", ApplicationOperations.TerminalWait);
        Assert.Equal("terminal.enter", SessionCapabilities.TerminalEnter);
        Assert.Equal("terminal.interrupt", SessionCapabilities.TerminalInterrupt);
        Assert.Equal("terminal.wait", SessionCapabilities.TerminalWait);
        Assert.Equal(
            3,
            new[]
            {
                ApplicationOperations.TerminalEnter,
                ApplicationOperations.TerminalInterrupt,
                ApplicationOperations.TerminalWait,
            }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Wait_inputs_require_finite_positive_bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalWaitForTextInput("ready", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalWaitForChangeInput(0, TimeSpan.FromMinutes(61)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalWaitForStableInput(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalWaitForChangeInput(-1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalWaitForPromptReadyInput(-1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalWaitForCommandFinishedInput(0, TimeSpan.FromHours(1).Add(
                TimeSpan.FromMilliseconds(1))));

        Assert.Equal(
            TimeSpan.FromHours(1),
            new TerminalWaitForDelayInput(TimeSpan.FromHours(1)).Delay);
        Assert.Equal(
            long.MaxValue,
            new TerminalWaitForPromptReadyInput(
                long.MaxValue,
                TimeSpan.FromHours(1)).AfterShellEventSequence);
    }

    [Fact]
    public void Changed_outcome_requires_a_newer_content_revision()
    {
        var snapshot = Screen(contentRevision: 3);

        Assert.Throws<ArgumentException>(() =>
            TerminalWaitOutcome.Changed(snapshot, initialContentRevision: 3));

        var changed = TerminalWaitOutcome.Changed(snapshot, initialContentRevision: 2);
        Assert.Equal(TerminalWaitOutcomeKind.Changed, changed.Kind);
        Assert.Equal(2, changed.InitialContentRevision);
        Assert.Equal(3, changed.ObservedContentRevision);
    }

    [Theory]
    [InlineData(TerminalWaitOutcomeKind.Elapsed)]
    [InlineData(TerminalWaitOutcomeKind.Matched)]
    [InlineData(TerminalWaitOutcomeKind.Changed)]
    [InlineData(TerminalWaitOutcomeKind.Stable)]
    public void Successful_wait_outcomes_require_the_observed_screen(
        TerminalWaitOutcomeKind kind)
    {
        Assert.Throws<ArgumentException>(() =>
            new TerminalWaitOutcome(kind, null, 0));
        Assert.Throws<ArgumentException>(() =>
            new TerminalWaitOutcome(kind, Screen(contentRevision: 0), null));
    }

    [Fact]
    public void Semantic_wait_outcomes_require_and_expose_the_exact_shell_event()
    {
        var prompt = new TerminalShellIntegrationEvent(
            3,
            TerminalCommandBoundaryKind.CommandInputStarted,
            DateTimeOffset.UnixEpoch);
        var finished = new TerminalShellIntegrationEvent(
            4,
            TerminalCommandBoundaryKind.CommandFinished,
            DateTimeOffset.UnixEpoch,
            ExitCode: 17);
        var screen = Screen(contentRevision: 8);

        var promptOutcome = TerminalWaitOutcome.PromptReady(
            screen,
            initialContentRevision: 7,
            prompt);
        var finishedOutcome = TerminalWaitOutcome.CommandFinished(
            screen,
            initialContentRevision: 7,
            finished);

        Assert.Same(prompt, promptOutcome.ObservedShellEvent);
        Assert.Equal(17, finishedOutcome.ObservedShellEvent!.ExitCode);
        Assert.Throws<ArgumentException>(() =>
            TerminalWaitOutcome.PromptReady(screen, 7, finished));
        Assert.Throws<ArgumentException>(() =>
            TerminalWaitOutcome.CommandFinished(screen, 7, prompt));
        Assert.Throws<ArgumentException>(() =>
            new TerminalWaitOutcome(
                TerminalWaitOutcomeKind.PromptReady,
                screen,
                InitialContentRevision: 7));
    }

    [Theory]
    [InlineData(TerminalWaitOutcomeKind.Timeout)]
    [InlineData(TerminalWaitOutcomeKind.Cancelled)]
    [InlineData(TerminalWaitOutcomeKind.SessionEnded)]
    [InlineData(TerminalWaitOutcomeKind.Unsupported)]
    public void Terminal_wait_end_states_can_precede_the_first_screen_read(
        TerminalWaitOutcomeKind kind)
    {
        var outcome = new TerminalWaitOutcome(kind, null, null);

        Assert.Null(outcome.Snapshot);
        Assert.Null(outcome.ObservedContentRevision);
    }

    [Fact]
    public void Key_repeat_is_bounded_and_explicit()
    {
        var repeated = new TerminalKeyStroke(
            TerminalKey.Backspace,
            TerminalKeyModifiers.None,
            RepeatCount: 12);

        Assert.Equal(12, repeated.RepeatCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalKeyStroke(
                TerminalKey.Backspace,
                TerminalKeyModifiers.None,
                RepeatCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalKeyStroke(
                TerminalKey.Backspace,
                TerminalKeyModifiers.None,
                TerminalKeyStroke.MaximumRepeatCount + 1));
    }

    [Fact]
    public void Rendered_screen_search_is_exact_bounded_and_revision_bound()
    {
        var snapshot = new TerminalScreenSnapshot(
            "header\nEDIT_PASS remains visible\nfooter",
            CursorRow: 2,
            CursorColumn: 0,
            Rows: 3,
            Columns: 80,
            IsAlternateScreen: true,
            WorkingDirectory: null,
            CapturedAtUtc: DateTimeOffset.UnixEpoch,
            ContentRevision: 17);

        var result = TerminalScreenFindResult.Search(
            snapshot,
            new TerminalScreenFindInput("EDIT_PASS", MaximumMatchCount: 4));

        var match = Assert.Single(result.Matches);
        Assert.Equal(17, result.ContentRevision);
        Assert.Equal(1, match.Line);
        Assert.Equal(0, match.Column);
        Assert.Equal("EDIT_PASS remains visible", match.LineText);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void Input_region_is_kept_only_when_it_fits_the_observed_viewport()
    {
        var now = DateTimeOffset.UnixEpoch;
        var state = new TerminalInteractiveStateSnapshot(
            Sequence: 1,
            TerminalInteractiveStateKind.IdleInput,
            now,
            now.AddMinutes(1),
            new TerminalInputRegion(Row: 2, StartColumn: 4, EndColumnExclusive: 20));

        var live = new TerminalScreenSnapshot(
            string.Empty,
            CursorRow: 0,
            CursorColumn: 0,
            Rows: 3,
            Columns: 20,
            IsAlternateScreen: true,
            WorkingDirectory: null,
            CapturedAtUtc: now,
            InteractiveState: state);
        var clipped = new TerminalScreenSnapshot(
            string.Empty,
            CursorRow: 0,
            CursorColumn: 0,
            Rows: 2,
            Columns: 20,
            IsAlternateScreen: true,
            WorkingDirectory: null,
            CapturedAtUtc: now,
            InteractiveState: state);

        Assert.Equal(state.InputRegion, live.InteractiveState?.InputRegion);
        Assert.Null(clipped.InteractiveState?.InputRegion);
    }

    private static TerminalScreenSnapshot Screen(long contentRevision) => new(
        string.Empty,
        0,
        0,
        24,
        80,
        false,
        null,
        DateTimeOffset.UnixEpoch,
        ContentRevision: contentRevision);
}
