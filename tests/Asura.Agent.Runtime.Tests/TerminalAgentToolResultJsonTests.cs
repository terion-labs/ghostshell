using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;

namespace Asura.Agent.Runtime.Tests;

public sealed class TerminalAgentToolResultJsonTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScreenResultIsBoundedLabeledAndRedactsObviousSecrets()
    {
        var longText = string.Concat(
            "password=hunter2\n",
            "safe output\n",
            "ghp_0123456789abcdef\n",
            string.Concat(Enumerable.Repeat("界", 20_000)));
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Screen(
                Screen(longText, contentRevision: 12)));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var text = root.GetProperty("text").GetString()!;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "untrusted_terminal",
            root.GetProperty("content_origin").GetString());
        Assert.Equal(12, root.GetProperty("content_revision").GetInt64());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, root.GetProperty("redactions").GetInt32());
        Assert.False(root.GetProperty("interactive_state_available").GetBoolean());
        Assert.False(root.GetProperty("input_region_available").GetBoolean());
        Assert.False(root.TryGetProperty("interactive_state", out _));
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 32 * 1024);
        Assert.DoesNotContain('\uFFFD', text);
    }

    [Fact]
    public void EscapedScreenAndScrollbackResultsFitTheKernelByteLimit()
    {
        var escapedScreen = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Screen(
                Screen(new string('"', 32 * 1024), contentRevision: 21)));
        Assert.True(
            Encoding.UTF8.GetByteCount(escapedScreen)
            <= AgentKernelLimits.Default.MaximumToolResultBytes);
        using (var screenDocument = JsonDocument.Parse(escapedScreen))
        {
            Assert.True(screenDocument.RootElement
                .GetProperty("truncated")
                .GetBoolean());
        }

        var rows = Enumerable.Range(0, TerminalScrollbackReadInput.LargeRead)
            .Select(index => new TerminalScrollbackRow(
                new TerminalScrollbackRowAnchor(22, index),
                new string('"', 8 * 1024)))
            .ToArray();
        var escapedHistory = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Scrollback(
                new TerminalScrollbackSnapshot(
                    rows,
                    rows.Length,
                    ContentRevision: 22,
                    HasMoreBefore: false,
                    HasMoreAfter: false)));

        Assert.True(
            Encoding.UTF8.GetByteCount(escapedHistory)
            <= AgentKernelLimits.Default.MaximumToolResultBytes);
        using var historyDocument = JsonDocument.Parse(escapedHistory);
        Assert.True(historyDocument.RootElement
            .GetProperty("truncated")
            .GetBoolean());
        Assert.True(historyDocument.RootElement
            .GetProperty("lines")
            .GetArrayLength() < rows.Length);
    }

    [Fact]
    public void MutationSuccessContainsNoTerminalContent()
    {
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Completed());

        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public void RenderedHistoryFindReturnsOpaqueJumpAnchorsAndBoundedText()
    {
        var result = new TerminalRenderedHistoryFindResult(
            [
                new TerminalRenderedHistoryRow(
                    new TerminalRenderedHistoryRowAnchor(31, 7),
                    "TUIHISTORY_START"),
            ],
            TotalRows: 20,
            ContentRevision: 31,
            IsTruncated: false);
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.RenderedHistoryFind(result));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var match = Assert.Single(root.GetProperty("matches").EnumerateArray());
        var encoded = match.GetProperty("row_anchor").GetString();

        Assert.Equal(20, root.GetProperty("total_rows").GetInt32());
        Assert.Equal("TUIHISTORY_START", match.GetProperty("text").GetString());
        Assert.True(TerminalRenderedHistoryAnchorCodec.TryDecode(encoded, out var anchor));
        Assert.Equal(new TerminalRenderedHistoryRowAnchor(31, 7), anchor);
        Assert.False(TerminalScrollbackAnchorCodec.TryDecode(encoded, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScreenResultReportsExactMouseTrackingState(bool enabled)
    {
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Screen(
                Screen(
                    "menu",
                    contentRevision: 4,
                    mouseTrackingEnabled: enabled)));

        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            enabled,
            document.RootElement
                .GetProperty("mouse_tracking_enabled")
                .GetBoolean());
    }

    [Fact]
    public void ScreenResultCarriesBoundedInteractiveAndShellState()
    {
        var shellEvents = Enumerable.Range(1, 40)
            .Select(sequence => new TerminalShellIntegrationEvent(
                sequence,
                sequence == 40
                    ? TerminalCommandBoundaryKind.CommandFinished
                    : TerminalCommandBoundaryKind.PromptStarted,
                Now.AddSeconds(sequence),
                sequence == 40 ? 17 : null))
            .ToArray();
        var snapshot = new TerminalScreenSnapshot(
            "ready",
            CursorRow: 0,
            CursorColumn: 0,
            Rows: 24,
            Columns: 80,
            IsAlternateScreen: false,
            WorkingDirectory: "/srv/work",
            CapturedAtUtc: Now,
            IsBracketedPasteEnabled: true,
            ContentRevision: 9,
            WindowTitle: "build shell",
            ScrollbackLinesAbove: 12,
            ScrollbackLinesBelow: 3,
            ShellIntegrationEvents: shellEvents);

        using var document = JsonDocument.Parse(
            TerminalAgentToolResultJson.Success(
                new AgentTerminalActionResult.Screen(snapshot)));
        var root = document.RootElement;

        Assert.Equal("/srv/work", root.GetProperty("working_directory").GetString());
        Assert.Equal("build shell", root.GetProperty("window_title").GetString());
        Assert.True(root.GetProperty("bracketed_paste_enabled").GetBoolean());
        Assert.Equal(12, root.GetProperty("scrollback_lines_above").GetInt32());
        Assert.Equal(3, root.GetProperty("scrollback_lines_below").GetInt32());
        Assert.False(root.GetProperty("viewport_at_bottom").GetBoolean());
        Assert.True(
            root.GetProperty("shell_integration_events_truncated").GetBoolean());
        var events = root.GetProperty("shell_integration_events");
        Assert.Equal(32, events.GetArrayLength());
        Assert.Equal(9, events[0].GetProperty("sequence").GetInt64());
        Assert.Equal(
            "command_finished",
            events[31].GetProperty("kind").GetString());
        Assert.Equal(17, events[31].GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public void ExplicitInteractiveStateIsLabeledUntrustedAndExpiring()
    {
        var snapshot = new TerminalScreenSnapshot(
            "Approve?",
            CursorRow: 0,
            CursorColumn: 0,
            Rows: 24,
            Columns: 80,
            IsAlternateScreen: true,
            WorkingDirectory: null,
            CapturedAtUtc: Now,
            ContentRevision: 10,
            InteractiveState: new TerminalInteractiveStateSnapshot(
                4,
                TerminalInteractiveStateKind.ApprovalRequired,
                Now,
                Now.AddSeconds(5),
                new TerminalInputRegion(
                    Row: 3,
                    StartColumn: 4,
                    EndColumnExclusive: 20)));

        using var document = JsonDocument.Parse(
            TerminalAgentToolResultJson.Success(
                new AgentTerminalActionResult.Screen(snapshot)));
        var state = document.RootElement.GetProperty("interactive_state");

        Assert.True(document.RootElement
            .GetProperty("interactive_state_available")
            .GetBoolean());
        Assert.True(document.RootElement
            .GetProperty("input_region_available")
            .GetBoolean());
        Assert.Equal("untrusted_terminal_protocol", state.GetProperty("origin").GetString());
        Assert.Equal("approval_required", state.GetProperty("state").GetString());
        Assert.Equal(4, state.GetProperty("sequence").GetInt64());
        Assert.Equal(Now, state.GetProperty("observed_at_utc").GetDateTimeOffset());
        Assert.Equal(Now.AddSeconds(5), state.GetProperty("expires_at_utc").GetDateTimeOffset());
        var inputRegion = state.GetProperty("input_region");
        Assert.Equal(3, inputRegion.GetProperty("row").GetInt32());
        Assert.Equal(4, inputRegion.GetProperty("start_column").GetInt32());
        Assert.Equal(
            20,
            inputRegion.GetProperty("end_column_exclusive").GetInt32());
    }

    [Fact]
    public void Screen_find_result_is_distinct_from_scrollback_and_redacted()
    {
        var result = new TerminalScreenFindResult(
            ContentRevision: 31,
            [
                new TerminalScreenFindResult.Match(
                    Offset: 8,
                    Line: 2,
                    Column: 4,
                    LineText: "token=do-not-project",
                    IsLineTruncated: false),
            ],
            IsTruncated: false);

        using var document = JsonDocument.Parse(
            TerminalAgentToolResultJson.Success(
                new AgentTerminalActionResult.ScreenFind(result)));
        var root = document.RootElement;
        var match = Assert.Single(root.GetProperty("matches").EnumerateArray());

        Assert.Equal(31, root.GetProperty("content_revision").GetInt64());
        Assert.Equal(1, root.GetProperty("match_count").GetInt32());
        Assert.False(root.TryGetProperty("total_lines", out _));
        Assert.Equal(2, match.GetProperty("line").GetInt32());
        Assert.DoesNotContain(
            "do-not-project",
            match.GetProperty("line_text").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Screen_diff_reports_missing_baseline_without_inventing_rows()
    {
        var result = new TerminalScreenDiffResult(
            InitialContentRevision: 7,
            CurrentContentRevision: 11,
            BaselineAvailable: false,
            ChangedRows: [],
            IsTruncated: false,
            CursorRow: 3,
            CursorColumn: 5,
            IsCursorVisible: true,
            InteractiveState: null);

        using var document = JsonDocument.Parse(
            TerminalAgentToolResultJson.Success(
                new AgentTerminalActionResult.ScreenDiff(result)));
        var root = document.RootElement;

        Assert.Equal(7, root.GetProperty("initial_content_revision").GetInt64());
        Assert.Equal(11, root.GetProperty("content_revision").GetInt64());
        Assert.False(root.GetProperty("baseline_available").GetBoolean());
        Assert.Empty(root.GetProperty("changed_rows").EnumerateArray());
        Assert.False(root.GetProperty("interactive_state_available").GetBoolean());
    }

    [Fact]
    public void HostFailureExposesStableFieldsButNotMessage()
    {
        var json = TerminalAgentToolResultJson.Failure(
            new HostError(
                HostErrorCode.EngineFailed,
                "engine_failed",
                "secret-canary",
                Retryable: true));

        Assert.DoesNotContain("secret-canary", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("engine_failed", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public void WaitResultCarriesOnlyBoundedScreenAndStableOutcome()
    {
        var snapshot = Screen("Selected", contentRevision: 8);
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Wait(
                TerminalWaitOutcome.Matched(snapshot, initialContentRevision: 7)));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("matched", root.GetProperty("wait_outcome").GetString());
        Assert.Equal(7, root.GetProperty("initial_content_revision").GetInt64());
        Assert.Equal("Selected", root.GetProperty("text").GetString());
    }

    [Fact]
    public void CommandFinishedWaitResultCarriesObservedEventAndExitCode()
    {
        var shellEvent = new TerminalShellIntegrationEvent(
            Sequence: 12,
            TerminalCommandBoundaryKind.CommandFinished,
            Now,
            ExitCode: 17);
        var snapshot = Screen(
            "done",
            contentRevision: 9,
            shellIntegrationEvents: [shellEvent]);
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Wait(
                TerminalWaitOutcome.CommandFinished(
                    snapshot,
                    initialContentRevision: 8,
                    shellEvent)));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            "command_finished",
            root.GetProperty("wait_outcome").GetString());
        Assert.Equal(
            12,
            root.GetProperty("observed_shell_event_sequence").GetInt64());
        Assert.Equal(
            "command_finished",
            root.GetProperty("observed_shell_event_kind").GetString());
        Assert.Equal(17, root.GetProperty("observed_exit_code").GetInt32());
    }

    [Fact]
    public void PromptReadyWaitResultDoesNotInventAnExitCode()
    {
        var shellEvent = new TerminalShellIntegrationEvent(
            Sequence: 4,
            TerminalCommandBoundaryKind.CommandInputStarted,
            Now);
        var snapshot = Screen(
            "$ ",
            contentRevision: 3,
            shellIntegrationEvents: [shellEvent]);
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Wait(
                TerminalWaitOutcome.PromptReady(
                    snapshot,
                    initialContentRevision: 2,
                    shellEvent)));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("prompt_ready", root.GetProperty("wait_outcome").GetString());
        Assert.False(root.TryGetProperty("observed_exit_code", out _));
    }

    private static TerminalScreenSnapshot Screen(
        string text,
        long contentRevision,
        bool mouseTrackingEnabled = false,
        IReadOnlyList<TerminalShellIntegrationEvent>? shellIntegrationEvents = null) =>
        new(
            text,
            CursorRow: 0,
            CursorColumn: 0,
            Rows: 24,
            Columns: 80,
            IsAlternateScreen: true,
            WorkingDirectory: "/srv/private",
            CapturedAtUtc: Now,
            IsMouseTrackingEnabled: mouseTrackingEnabled,
            ContentRevision: contentRevision,
            WindowTitle: "secret host",
            ShellIntegrationEvents: shellIntegrationEvents);
}
