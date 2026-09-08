using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Terminal.GhosttyVt;

namespace GhostShell.Terminal.Tests;

[Collection(GhosttyVtTestCollection.Name)]
public sealed class GhosttyVtTerminalSessionTests
{
    [Fact]
    public void Staged_runtime_exposes_the_pinned_rendering_features()
    {
        var availability = GhosttyVtTestRuntime.RequireStagedRuntime();

        Assert.NotEmpty(availability.Version!);
        Assert.Equal(1u, availability.ExtensionAbi);
        Assert.True(availability.SupportsKittyGraphics);
        Assert.NotNull(availability.LibraryPath);
    }

    [Fact]
    public void Runtime_probe_requires_every_declared_native_import()
    {
        var importedEntryPoints = typeof(GhosttyVtNative)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.GetCustomAttribute<LibraryImportAttribute>(inherit: false))
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.EntryPoint)
            .Where(entryPoint => !string.IsNullOrWhiteSpace(entryPoint))
            .Select(entryPoint => entryPoint!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(importedEntryPoints, GhosttyVtRuntimeProbe.RequiredExportsForTesting);
        Assert.Contains("ghostty_ghostshell_extension_abi", importedEntryPoints, StringComparer.Ordinal);
        Assert.Contains("ghostty_terminal_search", importedEntryPoints, StringComparer.Ordinal);
    }

    [Fact]
    public void Managed_extension_layouts_match_the_pinned_64_bit_abi()
    {
        Assert.True(
            GhosttyVtAbi.TryValidateManagedLayouts(out var detail),
            detail);
        Assert.Equal(24, Marshal.SizeOf<GhosttyVtSemanticPromptEvent>());
        Assert.Equal(40, Marshal.SizeOf<GhosttyVtTerminalSearchOptions>());
        Assert.Equal(32, Marshal.SizeOf<GhosttyVtTerminalSearchResult>());
        Assert.Equal(64, Marshal.SizeOf<GhosttyVtKittyVirtualPlacementRenderInfo>());
    }

    [Fact]
    public async Task Split_utf8_bytes_reach_one_canonical_render_state_without_replacement_text()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var bytes = Encoding.UTF8.GetBytes("界");

        await harness.Pty.WriteOutputBytesAsync(bytes.AsMemory(0, 1));
        await harness.Pty.WriteOutputBytesAsync(bytes.AsMemory(1));

        var frame = await WaitForFrameAsync(
            session,
            current => current.ViewportRows.SelectMany(row => row.Cells)
                .Any(cell => string.Equals(cell.Text, "界", StringComparison.Ordinal)));
        var cell = Assert.Single(
            frame.ViewportRows.SelectMany(row => row.Cells),
            candidate => string.Equals(candidate.Text, "界", StringComparison.Ordinal));
        Assert.Equal(TerminalRenderCellWidth.Wide, cell.Width);
        Assert.DoesNotContain(
            "�",
            string.Concat(frame.ViewportRows.SelectMany(row => row.Cells).Select(candidate => candidate.Text)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Soft_wrapped_rows_remain_one_logical_line_for_screen_reads_and_waits()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                Handle: 0,
                new ViewportDescriptor(40, 64, 1, Columns: 4, Rows: 4)),
            default);

        await harness.Pty.WriteOutputAsync("abcdEFGH");

        var snapshot = await WaitForScreenAsync(
            session,
            current => current.PlainText.Contains("abcdEFGH", StringComparison.Ordinal));
        Assert.True(snapshot.StructuredRows[0].IsWrapped);
        Assert.DoesNotContain("abcd\nEFGH", snapshot.PlainText, StringComparison.Ordinal);

        var wait = await session.WaitForTextAsync(
            new TerminalWaitForTextInput("cdEF", TimeSpan.FromSeconds(1)),
            default);
        Assert.Equal(TerminalWaitOutcomeKind.Matched, wait.Kind);
    }

    [Fact]
    public async Task Find_uses_ghostty_scrollback_wrapping_selection_and_navigation()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                Handle: 0,
                new ViewportDescriptor(64, 48, 1, Columns: 8, Rows: 3)),
            default);
        await harness.Pty.WriteOutputAsync(
            "old wrapped-needle\r\n" +
            "middle\r\n" +
            "wrapped-needle new\r\n" +
            "wrapped-needle latest\r\n" +
            "SEARCH_READY");
        _ = await WaitForScreenAsync(
            session,
            snapshot => snapshot.PlainText.Contains("SEARCH_READY", StringComparison.Ordinal));

        var middle = await session.FindAsync(new TerminalFindInput("wrapped-needle", 1), default);

        Assert.Equal(new TerminalFindResult(3, 1, false), middle);
        Assert.Equal(
            new TerminalSelectionText("wrapped-needle", true, false),
            await session.ReadSelectionAsync(default));

        var oldest = await session.FindAsync(new TerminalFindInput("wrapped-needle", -1), default);
        Assert.Equal(new TerminalFindResult(3, 2, false), oldest);
        var scrolled = await session.ReadScreenAsync(default);
        Assert.True(scrolled.ScrollbackLinesBelow > 0);

        Assert.Equal(
            TerminalFindResult.Empty,
            await session.FindAsync(new TerminalFindInput(string.Empty), default));
        Assert.Equal(
            new TerminalSelectionText(string.Empty, false, false),
            await session.ReadSelectionAsync(default));
    }

    [Fact]
    public async Task Find_bounds_history_scans_at_4096_results()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var output = string.Concat(Enumerable.Repeat("bounded-hit\r\n", 4_100))
            + "BOUNDED_SEARCH_READY";
        await harness.Pty.WriteOutputAsync(output);
        _ = await WaitForScreenAsync(
            session,
            snapshot => snapshot.PlainText.Contains("BOUNDED_SEARCH_READY", StringComparison.Ordinal));

        var result = await session.FindAsync(new TerminalFindInput("bounded-hit"), default);

        Assert.Equal(4_096, result.MatchCount);
        Assert.Equal(0, result.SelectedMatchIndex);
        Assert.True(result.IsScanTruncated);
    }

    [Fact]
    public async Task ProjectedHistoryReadAndFindDoNotMutateViewportOrSelection()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                Handle: 0,
                new ViewportDescriptor(80, 64, 1, Columns: 10, Rows: 4)),
            default);
        var output = string.Concat(
            Enumerable.Range(0, 80).Select(index =>
                $"history-{index:D3}-needle\r\n"))
            + "PROJECTION_READY";
        await harness.Pty.WriteOutputAsync(output);
        _ = await WaitForScreenAsync(
            session,
            snapshot => snapshot.PlainText.Contains(
                "PROJECTION_READY",
                StringComparison.Ordinal));
        _ = await session.FindAsync(
            new TerminalFindInput("history-040-needle"),
            default);
        var screenBefore = await session.ReadScreenAsync(default);
        var selectionBefore = await session.ReadSelectionAsync(default);

        var projected = await session.ReadScrollbackAsync(
            new TerminalScrollbackReadInput(
                TerminalScrollbackReadOrigin.Bottom,
                TerminalScrollbackReadInput.MediumRead),
            default);
        var found = await session.FindScrollbackAsync(
            new TerminalScrollbackFindInput(
                "needle",
                TerminalScrollbackFindDirection.Backward,
                MaximumMatchCount: 4),
            default);
        var screenAfter = await session.ReadScreenAsync(default);
        var selectionAfter = await session.ReadSelectionAsync(default);

        Assert.NotEmpty(projected.Rows);
        Assert.Equal(4, found.Matches.Count);
        Assert.Equal(screenBefore.ContentRevision, projected.ContentRevision);
        Assert.Equal(screenBefore.ContentRevision, found.ContentRevision);
        Assert.Equal(screenBefore.ContentRevision, screenAfter.ContentRevision);
        Assert.Equal(screenBefore.PlainText, screenAfter.PlainText);
        Assert.Equal(
            screenBefore.ScrollbackLinesAbove,
            screenAfter.ScrollbackLinesAbove);
        Assert.Equal(
            screenBefore.ScrollbackLinesBelow,
            screenAfter.ScrollbackLinesBelow);
        Assert.Equal(selectionBefore, selectionAfter);
    }

    [Fact]
    public async Task ProjectedFindStopsAtTheExplicitHistoryScanCap()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var output = string.Concat(
            Enumerable.Repeat("bounded-projection-row\r\n", 4_200))
            + "PROJECTED_SEARCH_READY";
        await harness.Pty.WriteOutputAsync(output);
        _ = await WaitForScreenAsync(
            session,
            snapshot => snapshot.PlainText.Contains(
                "PROJECTED_SEARCH_READY",
                StringComparison.Ordinal));

        var result = await session.FindScrollbackAsync(
            new TerminalScrollbackFindInput(
                "absent-projection-needle",
                TerminalScrollbackFindDirection.Forward,
                MaximumMatchCount: 1),
            default);

        Assert.True(
            result.TotalLines
            > GhosttyVtTerminalSession.MaximumProjectedFindScanRows);
        Assert.Empty(result.Matches);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public async Task ProjectedFindSearchesAcrossBlankScrollbackRows()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                Handle: 0,
                new ViewportDescriptor(80, 64, 1, Columns: 40, Rows: 4)),
            default);
        await harness.Pty.WriteOutputAsync(
            "LONG_START\r\n\r\nitem 2\r\n\r\nitem 3\r\n\r\n"
            + "item 4\r\n\r\nLONG_END\r\nREADY");
        _ = await WaitForScreenAsync(
            session,
            snapshot => snapshot.PlainText.Contains("READY", StringComparison.Ordinal));

        var result = await session.FindScrollbackAsync(
            new TerminalScrollbackFindInput(
                "LONG_START",
                TerminalScrollbackFindDirection.Backward,
                MaximumMatchCount: 4),
            default);

        Assert.Single(result.Matches);
        Assert.Contains("LONG_START", result.Matches[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderedHistoryFindIncludesCurrentTuiRowsWithoutMutatingViewport()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                Handle: 0,
                new ViewportDescriptor(80, 64, 1, Columns: 40, Rows: 4)),
            default);
        await harness.Pty.WriteOutputAsync("ACTIVE_TUI_ONLY_NEEDLE");
        var before = await WaitForScreenAsync(
            session,
            snapshot => snapshot.PlainText.Contains(
                "ACTIVE_TUI_ONLY_NEEDLE",
                StringComparison.Ordinal));

        var scrollback = await session.FindScrollbackAsync(
            new TerminalScrollbackFindInput(
                "ACTIVE_TUI_ONLY_NEEDLE",
                TerminalScrollbackFindDirection.Backward,
                MaximumMatchCount: 4),
            default);
        var rendered = await session.FindRenderedHistoryAsync(
            new TerminalRenderedHistoryFindInput(
                "ACTIVE_TUI_ONLY_NEEDLE",
                TerminalScrollbackFindDirection.Backward,
                MaximumMatchCount: 4),
            default);
        var after = await session.ReadScreenAsync(default);

        Assert.Empty(scrollback.Matches);
        Assert.Single(rendered.Matches);
        Assert.Equal(before.ContentRevision, rendered.ContentRevision);
        Assert.Equal(before.ContentRevision, after.ContentRevision);
        Assert.Equal(before.PlainText, after.PlainText);
    }

    [Fact]
    public async Task RenderedHistoryAnchorJumpsViewportToAnOffscreenTuiRow()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                Handle: 0,
                new ViewportDescriptor(80, 64, 1, Columns: 40, Rows: 4)),
            default);
        var output = string.Concat(
            Enumerable.Range(0, 20).Select(index => $"rendered-{index:D3}\r\n"))
            + "RENDERED_READY";
        await harness.Pty.WriteOutputAsync(output);
        var before = await WaitForScreenAsync(
            session,
            snapshot => snapshot.PlainText.Contains(
                "RENDERED_READY",
                StringComparison.Ordinal));
        Assert.DoesNotContain("rendered-005", before.PlainText, StringComparison.Ordinal);

        var found = await session.FindRenderedHistoryAsync(
            new TerminalRenderedHistoryFindInput(
                "rendered-005",
                TerminalScrollbackFindDirection.Forward,
                MaximumMatchCount: 1),
            default);
        var match = Assert.Single(found.Matches);

        await session.JumpToRenderedHistoryAsync(match.Anchor, default);
        var after = await session.ReadScreenAsync(default);

        Assert.Contains("rendered-005", after.PlainText, StringComparison.Ordinal);
        Assert.True(after.ContentRevision > before.ContentRevision);
        await Assert.ThrowsAsync<TerminalRenderedHistoryAnchorStaleException>(
            () => session.JumpToRenderedHistoryAsync(match.Anchor, default).AsTask());
    }

    [Fact]
    public async Task Render_frame_preserves_terminal_cursor_underline_color_and_damage()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var initial = await session.ReadRenderFrameAsync(default);
        var clean = await session.ReadRenderFrameAsync(default);

        Assert.Equal(initial.Revision, clean.Revision);
        Assert.Equal(TerminalRenderDamageKind.None, clean.Delta.Kind);

        await harness.Pty.WriteOutputAsync(
            "\u001b[6 q\u001b[4:3;58:2::12:34:56mU\u001b[0m");

        var changed = await WaitForFrameAsync(
            session,
            frame => frame.Revision > initial.Revision
                && string.Equals(frame.ViewportRows[0].Cells[0].Text, "U", StringComparison.Ordinal));
        var underlined = changed.ViewportRows[0].Cells[0];

        Assert.Equal(TerminalCursorVisualStyle.Bar, changed.Cursor.VisualStyle);
        Assert.False(changed.Cursor.IsBlinking);
        Assert.Equal(TerminalUnderlineKind.Curly, underlined.Underline);
        Assert.Equal(
            new TerminalCellColor(TerminalColorMode.Rgb, 0x0C2238),
            underlined.UnderlineColor);
        Assert.Equal(TerminalRenderDamageKind.Partial, changed.Delta.Kind);
        Assert.Contains(0, changed.Delta.DirtyRows);

        var acknowledged = await session.ReadRenderFrameAsync(default);
        Assert.Equal(changed.Revision, acknowledged.Revision);
        Assert.Equal(TerminalRenderDamageKind.None, acknowledged.Delta.Kind);
    }

    [Fact]
    public async Task Render_frame_distinguishes_default_backgrounds_from_explicit_sgr_colors()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        await harness.Pty.WriteOutputAsync("\u001b[48;2;17;34;51mX\u001b[0mY");

        var frame = await WaitForFrameAsync(
            session,
            current => string.Equals(current.ViewportRows[0].Cells[0].Text, "X"
, StringComparison.Ordinal) && string.Equals(current.ViewportRows[0].Cells[1].Text, "Y", StringComparison.Ordinal));
        var explicitBackground = frame.ViewportRows[0].Cells[0];
        var defaultBackground = frame.ViewportRows[0].Cells[1];
        var blankBackground = frame.ViewportRows[0].Cells[2];

        Assert.Equal(
            new TerminalCellColor(TerminalColorMode.Rgb, 0x112233),
            explicitBackground.Background);
        Assert.Equal(TerminalColorMode.Default, defaultBackground.Background.Mode);
        Assert.Equal(TerminalColorMode.Default, blankBackground.Background.Mode);
    }

    [Fact]
    public async Task Osc133_emits_durable_semantic_command_lifecycle_events()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        await harness.Pty.WriteOutputAsync(
            "\u001b]133;A\u0007$ " +
            "\u001b]133;B\u0007echo ok" +
            "\u001b]133;C\u0007\r\nok\r\n" +
            "\u001b]133;D;7\u0007");

        var snapshot = await WaitForScreenAsync(
            session,
            current => current.ShellIntegrationEvents.Count == 4);

        Assert.Equal(
            [
                TerminalCommandBoundaryKind.PromptStarted,
                TerminalCommandBoundaryKind.CommandInputStarted,
                TerminalCommandBoundaryKind.CommandExecuted,
                TerminalCommandBoundaryKind.CommandFinished,
            ],
            snapshot.ShellIntegrationEvents.Select(shellEvent => shellEvent.Kind));
        Assert.Equal([1L, 2L, 3L, 4L],
            snapshot.ShellIntegrationEvents.Select(shellEvent => shellEvent.Sequence));
        Assert.Equal(7, snapshot.ShellIntegrationEvents[^1].ExitCode);

        await harness.Pty.WriteOutputAsync("after-event");
        var later = await WaitForScreenAsync(
            session,
            current => current.PlainText.Contains("after-event", StringComparison.Ordinal));
        Assert.Equal(4, later.ShellIntegrationEvents.Count);
    }

    [Fact]
    public async Task SemanticWaitsMatchOnlyNewExactOsc133EventsAndExposeExitCode()
    {
        var harness = CreateWithShellIntegrationSupport();
        await using var session = harness.Session;

        var promptWaiting = session.WaitForPromptReadyAsync(
            new TerminalWaitForPromptReadyInput(
                AfterShellEventSequence: 0,
                TimeSpan.FromSeconds(2)),
            default).AsTask();
        await harness.Pty.WriteOutputAsync("\u001b]133;A\u0007$ ");
        _ = await WaitForScreenAsync(
            session,
            current => current.ShellIntegrationEvents.Count == 1);
        Assert.False(promptWaiting.IsCompleted);

        await harness.Pty.WriteOutputAsync("\u001b]133;B\u0007");
        var prompt = await promptWaiting.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TerminalWaitOutcomeKind.PromptReady, prompt.Kind);
        Assert.Equal(2, prompt.ObservedShellEvent?.Sequence);
        Assert.Equal(
            TerminalCommandBoundaryKind.CommandInputStarted,
            prompt.ObservedShellEvent?.Kind);

        var commandWaiting = session.WaitForCommandFinishedAsync(
            new TerminalWaitForCommandFinishedInput(
                AfterShellEventSequence: 2,
                TimeSpan.FromSeconds(2)),
            default).AsTask();
        await harness.Pty.WriteOutputAsync("echo ok\u001b]133;C\u0007\r\nok\r\n");
        _ = await WaitForScreenAsync(
            session,
            current => current.ShellIntegrationEvents.Count == 3);
        Assert.False(commandWaiting.IsCompleted);

        await harness.Pty.WriteOutputAsync("\u001b]133;D;17\u0007");
        var finished = await commandWaiting.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TerminalWaitOutcomeKind.CommandFinished, finished.Kind);
        Assert.Equal(4, finished.ObservedShellEvent?.Sequence);
        Assert.Equal(17, finished.ObservedShellEvent?.ExitCode);

        var afterPreviousFinish = session.WaitForCommandFinishedAsync(
            new TerminalWaitForCommandFinishedInput(
                AfterShellEventSequence: 4,
                TimeSpan.FromSeconds(2)),
            default).AsTask();
        Assert.False(afterPreviousFinish.IsCompleted);
        await harness.Pty.WriteOutputAsync(
            "\u001b]133;C\u0007\u001b]133;D;23\u0007");
        var nextFinished = await afterPreviousFinish.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Equal(6, nextFinished.ObservedShellEvent?.Sequence);
        Assert.Equal(23, nextFinished.ObservedShellEvent?.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactoryPropagatesUnavailableShellIntegrationStatusToSemanticWaits(
        bool disabledExplicitly)
    {
        _ = GhosttyVtTestRuntime.RequireStagedRuntime();
        var ptyFactory = new FakePortablePtyFactory();
        var factory = new GhosttyVtTerminalSessionFactory(ptyFactory);
        var launch = disabledExplicitly
            ? new TerminalLaunchRequest(
                Environment.CurrentDirectory,
                renderProfile: new TerminalRenderProfileSnapshot(
                    13,
                    TerminalCursorStyle.Block,
                    cursorBlink: false,
                    scrollbackLines: 10_000,
                    TerminalPalette.GhostShellDark,
                    shellIntegration: TerminalShellIntegrationMode.Disabled))
            : new TerminalLaunchRequest(
                Environment.CurrentDirectory,
                executable: "/usr/bin/python3");
        await using var session = await factory.CreateAsync(
            SessionId.New(),
            launch,
            default);

        var prompt = await session.WaitForPromptReadyAsync(
            new TerminalWaitForPromptReadyInput(0, TimeSpan.FromHours(1)),
            default);
        var command = await session.WaitForCommandFinishedAsync(
            new TerminalWaitForCommandFinishedInput(0, TimeSpan.FromHours(1)),
            default);

        Assert.Equal(TerminalWaitOutcomeKind.Unsupported, prompt.Kind);
        Assert.Equal(TerminalWaitOutcomeKind.Unsupported, command.Kind);
        Assert.Null(prompt.Snapshot);
        Assert.Null(command.ObservedShellEvent);
    }

    /// <summary>
    /// The two sequences a program actually uses to ask for attention, plus the
    /// bell. Agents and long builds send these from panels nobody is looking
    /// at, which is the whole reason the shell carries them.
    /// </summary>
    [Fact]
    public async Task Osc9_osc777_and_the_bell_are_published_as_notifications()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var collected = new List<PanelNotificationEvent>();
        var watching = Task.Run(
            async () =>
            {
                await foreach (var notification in session
                    .WatchNotificationsAsync(0, lifetime.Token)
                    .ConfigureAwait(false))
                {
                    lock (collected)
                    {
                        collected.Add(notification);
                    }

                    if (collected.Count == 3)
                    {
                        return;
                    }
                }
            },
            lifetime.Token);

        await harness.Pty.WriteOutputAsync(
            "\u001b]9;Build finished\u0007"
            + "\u001b]777;notify;Agent;Waiting for input\u0007"
            + "\u0007");

        await watching.WaitAsync(TimeSpan.FromSeconds(10));

        PanelNotificationEvent[] notifications;
        lock (collected)
        {
            notifications = [.. collected];
        }

        Assert.Equal(
            [
                PanelNotificationKind.Notification,
                PanelNotificationKind.Notification,
                PanelNotificationKind.Bell,
            ],
            notifications.Select(notification => notification.Kind));
        // OSC 9 carries a body and no title; OSC 777 carries both.
        Assert.Equal("Build finished", notifications[0].Body);
        Assert.Equal("Agent", notifications[1].Title);
        Assert.Equal("Waiting for input", notifications[1].Body);
        Assert.Equal(string.Empty, notifications[2].Body);
        Assert.Equal([1L, 2L, 3L], notifications.Select(notification => notification.Sequence));
    }

    [Fact]
    public async Task Interactive_state_protocol_is_exposed_on_screen()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        await harness.Pty.WriteOutputAsync(
            "\u001b]777;notify;terminal.interactive-state.v1;"
            + "{\"sequence\":1,\"state\":\"approval_required\",\"ttl_ms\":5000}\u0007");

        var screen = await WaitForScreenAsync(
            session,
            snapshot => snapshot.InteractiveState is not null);
        var state = Assert.IsType<TerminalInteractiveStateSnapshot>(screen.InteractiveState);

        Assert.Equal(1, state.Sequence);
        Assert.Equal(TerminalInteractiveStateKind.ApprovalRequired, state.Kind);
        Assert.True(state.ExpiresAtUtc > state.ObservedAtUtc);
    }

    /// <summary>
    /// A watcher that reconnects says what it has already seen, and must not be
    /// told about it twice.
    /// </summary>
    [Fact]
    public async Task A_notification_watcher_can_resume_after_what_it_already_saw()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await harness.Pty.WriteOutputAsync("\u001b]9;first\u0007\u001b]9;second\u0007");

        var resumed = new List<PanelNotificationEvent>();
        await foreach (var notification in session
            .WatchNotificationsAsync(1, lifetime.Token)
            .ConfigureAwait(false))
        {
            resumed.Add(notification);
            if (resumed.Count == 1)
            {
                break;
            }
        }

        Assert.Equal("second", Assert.Single(resumed).Body);
    }

    [Fact]
    public async Task Notification_queue_enforces_field_and_aggregate_utf8_budgets()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var oversizedBody = string.Concat(Enumerable.Repeat(
            "\U0001F680",
            PanelNotificationTextBudget.MaximumBodyUtf8Bytes / 4 + 1));

        for (var index = 0; index < 100; index++)
        {
            session.PublishNotification(
                PanelNotificationKind.Notification,
                "Agent",
                oversizedBody);
        }

        Assert.InRange(
            session.RetainedNotificationUtf8Bytes,
            1,
            GhosttyVtTerminalSession.MaximumQueuedNotificationUtf8Bytes);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var notifications = session
            .WatchNotificationsAsync(0, lifetime.Token)
            .GetAsyncEnumerator(lifetime.Token);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(
            PanelNotificationTextBudget.MaximumBodyUtf8Bytes,
            Encoding.UTF8.GetByteCount(notifications.Current.Body));
    }

    [Fact]
    public async Task Graceful_close_uses_shell_lifecycle_instead_of_treating_every_shell_as_busy()
    {
        var idleHarness = await CreateAsync();
        await using (var idle = idleHarness.Session)
        {
            await idleHarness.Pty.WriteOutputAsync("\u001b]133;A\u0007$ \u001b]133;B\u0007");
            _ = await WaitForScreenAsync(
                idle,
                current => current.ShellIntegrationEvents.Count == 2);

            var idleSnapshot = await idle.SnapshotAsync(default);
            Assert.False(idleSnapshot.HasActiveWork);
            Assert.Equal(
                PanelCloseOutcome.GracefullyClosed,
                await idle.CloseAsync(PanelCloseMode.Graceful, default));
            Assert.Equal(0, idleHarness.Pty.KillCount);
        }

        var runningHarness = await CreateAsync();
        await using var running = runningHarness.Session;
        await runningHarness.Pty.WriteOutputAsync(
            "\u001b]133;A\u0007$ \u001b]133;B\u0007sleep 10\u001b]133;C\u0007");
        _ = await WaitForScreenAsync(
            running,
            current => current.ShellIntegrationEvents.Count == 3);

        var runningSnapshot = await running.SnapshotAsync(default);
        Assert.True(runningSnapshot.HasActiveWork);
        Assert.Equal(
            PanelCloseOutcome.ConfirmationRequired,
            await running.CloseAsync(PanelCloseMode.Graceful, default));

        await runningHarness.Pty.WriteOutputAsync(
            "\u001b]133;D;0\u0007\u001b]133;A\u0007$ \u001b]133;B\u0007");
        _ = await WaitForScreenAsync(
            running,
            current => current.ShellIntegrationEvents.Count == 6);

        var returnedToPrompt = await running.SnapshotAsync(default);
        Assert.False(returnedToPrompt.HasActiveWork);
        Assert.Equal(
            PanelCloseOutcome.GracefullyClosed,
            await running.CloseAsync(PanelCloseMode.Graceful, default));
    }

    [Fact]
    public async Task MultiplexedRunningCommandDoesNotRequireBusyConfirmation()
    {
        var harness = await CreateAsync(new TerminalLaunchRequest(
            Environment.CurrentDirectory,
            multiplexerSession: new TerminalMultiplexerSession(
                TerminalMultiplexingMode.Automatic,
                "ghostshell-1234abcd",
                isEstablished: true)));
        await using var session = harness.Session;
        await harness.Pty.WriteOutputAsync(
            "\u001b]133;A\u0007$ \u001b]133;B\u0007top\u001b]133;C\u0007");
        _ = await WaitForScreenAsync(
            session,
            current => current.ShellIntegrationEvents.Count == 3);

        var snapshot = await session.SnapshotAsync(default);

        Assert.False(snapshot.HasActiveWork);
        Assert.Equal("protected by remote multiplexer", snapshot.StatusDetail);
        Assert.Equal(
            PanelCloseOutcome.GracefullyClosed,
            await session.CloseAsync(PanelCloseMode.Graceful, default));
        Assert.Equal(0, harness.Pty.KillCount);
    }

    [Fact]
    public async Task DockerExecShellAtContainerPromptClosesWithoutFalseWorkingWarning()
    {
        var harness = await CreateAsync(new TerminalLaunchRequest(
            Environment.CurrentDirectory,
            shellActivityFallback: TerminalShellActivityFallback.PromptShape));
        await using var session = harness.Session;
        await harness.Pty.WriteOutputAsync(
            "\u001b]133;A\u0007host% \u001b]133;B\u0007"
            + "docker exec --interactive --tty grafana /bin/sh\u001b]133;C\u0007"
            + "\r\n/usr/share/grafana $ ");
        _ = await WaitForScreenAsync(
            session,
            current => current.ShellIntegrationEvents.Count == 3
                && current.PlainText.Contains("/usr/share/grafana $", StringComparison.Ordinal));

        var snapshot = await session.SnapshotAsync(default);

        Assert.False(snapshot.HasActiveWork);
        Assert.Equal(
            PanelCloseOutcome.GracefullyClosed,
            await session.CloseAsync(PanelCloseMode.Graceful, default));
    }

    [Fact]
    public async Task WorkspaceIsolatePromptClosesWithoutFalseWorkingWarning()
    {
        var harness = await CreateAsync(new TerminalLaunchRequest(
            Environment.CurrentDirectory,
            shellActivityFallback: TerminalShellActivityFallback.PromptShape));
        await using var session = harness.Session;
        await harness.Pty.WriteOutputAsync(
            "ghostshell-5cce9f181cf6e094c296cea0:~# ");
        _ = await WaitForScreenAsync(
            session,
            current => current.PlainText.Contains(
                "ghostshell-5cce9f181cf6e094c296cea0:~#",
                StringComparison.Ordinal));

        var snapshot = await session.SnapshotAsync(default);

        Assert.False(snapshot.HasActiveWork);
        Assert.Equal(
            PanelCloseOutcome.GracefullyClosed,
            await session.CloseAsync(PanelCloseMode.Graceful, default));
    }

    [Fact]
    public async Task SoftWrappedWorkspaceIsolatePromptClosesWithoutFalseWorkingWarning()
    {
        var harness = await CreateAsync(new TerminalLaunchRequest(
            Environment.CurrentDirectory,
            shellActivityFallback: TerminalShellActivityFallback.PromptShape));
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                Handle: 0,
                new ViewportDescriptor(256, 96, 1, Columns: 32, Rows: 6)),
            default);
        const string prompt = "root@Ubuntu-2404-noble-amd64-base ~ # ";
        await harness.Pty.WriteOutputAsync(prompt);
        var screen = await WaitForScreenAsync(
            session,
            current => current.PlainText.Contains(prompt.TrimEnd(), StringComparison.Ordinal));
        Assert.True(screen.StructuredRows[0].IsWrapped);
        Assert.True(screen.CursorRow > 0);

        var snapshot = await session.SnapshotAsync(default);

        Assert.False(snapshot.HasActiveWork);
        Assert.Equal("waiting at a prompt", snapshot.StatusDetail);
        Assert.Equal(
            PanelCloseOutcome.GracefullyClosed,
            await session.CloseAsync(PanelCloseMode.Graceful, default));
    }

    [Fact]
    public async Task OrdinaryRunningCommandThatPrintsPromptShapeStillRequiresConfirmation()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await harness.Pty.WriteOutputAsync(
            "\u001b]133;A\u0007$ \u001b]133;B\u0007"
            + "printf '$ '\u001b]133;C\u0007");
        _ = await WaitForScreenAsync(
            session,
            current => current.ShellIntegrationEvents.Count == 3
                && current.PlainText.Contains("$", StringComparison.Ordinal));

        var snapshot = await session.SnapshotAsync(default);

        Assert.True(snapshot.HasActiveWork);
        Assert.Equal(
            PanelCloseOutcome.ConfirmationRequired,
            await session.CloseAsync(PanelCloseMode.Graceful, default));
    }

    [Fact]
    public async Task Kitty_image_content_and_placement_follow_the_terminal_storage_lifecycle()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var redPixel = Convert.ToBase64String([255, 0, 0, 255]);
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                Handle: 0,
                new ViewportDescriptor(800, 384, 1.5, Columns: 80, Rows: 24)),
            default);

        await harness.Pty.WriteOutputAsync(
            $"\u001b_Ga=T,f=32,s=1,v=1,i=7,p=9,X=1,Y=2;{redPixel}\u001b\\");

        var displayed = await WaitForFrameAsync(
            session,
            frame => frame.KittyGraphics.Placements.Count == 1);
        var placement = Assert.Single(displayed.KittyGraphics.Placements);
        var image = Assert.Single(displayed.KittyGraphics.Images.Values);

        Assert.Equal((uint)7, image.Key.ImageId);
        Assert.Equal((uint)9, placement.PlacementId);
        Assert.Equal(TerminalKittyImagePixelFormat.Rgba, image.PixelFormat);
        Assert.Equal([255, 0, 0, 255], image.Pixels.ToArray());
        Assert.Equal(image.Key, placement.Image);
        Assert.NotNull(placement.Geometry);
        Assert.Equal(2d / 3d, placement.PixelOffsetX, precision: 5);
        Assert.Equal(4d / 3d, placement.PixelOffsetY, precision: 5);

        await harness.Pty.WriteOutputAsync(string.Concat(Enumerable.Repeat("\r\n", 30)));
        var scrolledOffscreen = await WaitForFrameAsync(
            session,
            frame => frame.Revision > displayed.Revision
                && frame.KittyGraphics.Placements.Count == 0);
        Assert.Empty(scrolledOffscreen.KittyGraphics.Images);

        await harness.Pty.WriteOutputAsync("\u001b_Ga=d,d=I,i=7\u001b\\");
        var deleted = await WaitForFrameAsync(
            session,
            frame => frame.Revision > scrolledOffscreen.Revision
                && frame.KittyGraphics.Placements.Count == 0);

        Assert.Empty(deleted.KittyGraphics.Images);
    }

    [Fact]
    public async Task Kitty_unicode_placeholder_resolves_to_virtual_placement_geometry()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                Handle: 0,
                new ViewportDescriptor(800, 384, 2, Columns: 80, Rows: 24)),
            default);
        var redPixel = Convert.ToBase64String([255, 0, 0, 255]);

        await harness.Pty.WriteOutputAsync(
            $"\u001b_Ga=t,f=32,s=1,v=1,i=8;{redPixel}\u001b\\" +
            "\u001b_Ga=p,i=8,U=1,c=1,r=1\u001b\\" +
            "\u001b[38;5;8m\U0010EEEE\u0305\u0305\u001b[39m");

        var frame = await WaitForFrameAsync(
            session,
            current => current.KittyGraphics.Placements.Any(placement => placement.IsVirtual));
        var placement = Assert.Single(
            frame.KittyGraphics.Placements,
            candidate => candidate.IsVirtual);

        Assert.Equal((uint)8, placement.Image.ImageId);
        Assert.NotNull(placement.Geometry);
        Assert.Equal(0, placement.Geometry.Value.ViewportColumn);
        Assert.Equal(0, placement.Geometry.Value.ViewportRow);
        Assert.Equal(10, placement.Geometry.Value.PixelWidth);
        Assert.Equal(10, placement.Geometry.Value.PixelHeight);
        Assert.Equal(TerminalKittyPlacementLayer.BelowText, placement.Layer);
        Assert.True(frame.ViewportRows[0].ContainsKittyVirtualPlaceholder);
    }

    [Fact]
    public async Task Input_bytes_are_acknowledged_only_after_the_portable_pty_flushes()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.PauseInputFlushes();

        var write = session.WriteAsync("λ-input", default).AsTask();
        try
        {
            await harness.Pty.WaitForInputFlushAttemptAsync();

            Assert.Equal("λ-input", harness.Pty.WrittenText);
            Assert.False(write.IsCompleted);
        }
        finally
        {
            harness.Pty.ResumeInputFlushes();
        }

        await write;
    }

    [Fact]
    public async Task Kitty_keyboard_protocol_receives_press_repeat_and_release_actions()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var beforeModeChange = await session.ReadRenderFrameAsync(default);
        await harness.Pty.WriteOutputAsync("\u001b[>31u");
        _ = await WaitForFrameAsync(
            session,
            frame => frame.Revision > beforeModeChange.Revision);

        await session.SendPhysicalKeyAsync(
            PhysicalKey(TerminalKeyAction.Press),
            default);
        await session.SendPhysicalKeyAsync(
            PhysicalKey(TerminalKeyAction.Repeat),
            default);
        await session.SendPhysicalKeyAsync(
            PhysicalKey(TerminalKeyAction.Release, text: string.Empty),
            default);

        Assert.Equal(
            "\u001b[97;;97u\u001b[97;1:2;97u\u001b[97;1:3u",
            harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Kitty_keyboard_protocol_preserves_modifiers_and_consumed_modifiers()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var beforeModeChange = await session.ReadRenderFrameAsync(default);
        await harness.Pty.WriteOutputAsync("\u001b[>31u");
        _ = await WaitForFrameAsync(
            session,
            frame => frame.Revision > beforeModeChange.Revision);

        await session.SendPhysicalKeyAsync(
            new TerminalPhysicalKeyEvent(
                TerminalPhysicalKey.A,
                "A",
                "A",
                TerminalKeyModifiers.Shift | TerminalKeyModifiers.Control,
                TerminalKeyModifiers.None,
                TerminalKeyAction.Press,
                'a'),
            default);
        await session.SendPhysicalKeyAsync(
            new TerminalPhysicalKeyEvent(
                TerminalPhysicalKey.A,
                "A",
                "A",
                TerminalKeyModifiers.Shift | TerminalKeyModifiers.Control,
                TerminalKeyModifiers.Shift,
                TerminalKeyAction.Press,
                'a'),
            default);

        Assert.Equal("\u001b[97:65;6u\u001b[97:65;6u", harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Consumed_layout_modifiers_are_not_reencoded_as_terminal_modifiers()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        await session.SendPhysicalKeyAsync(
            new TerminalPhysicalKeyEvent(
                TerminalPhysicalKey.Digit8,
                "D8",
                "[",
                TerminalKeyModifiers.Alt,
                TerminalKeyModifiers.Alt,
                TerminalKeyAction.Press,
                '8'),
            default);

        Assert.Equal("[", harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Focus_reporting_emits_both_gain_and_loss_sequences()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var beforeModeChange = await session.ReadRenderFrameAsync(default);
        await harness.Pty.WriteOutputAsync("\u001b[?1004h");
        _ = await WaitForFrameAsync(
            session,
            frame => frame.Revision > beforeModeChange.Revision);

        await session.FocusAsync(default);
        await session.BlurAsync(default);

        Assert.Equal("\u001b[I\u001b[O", harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Revision_bound_mouse_is_rejected_when_output_changes_while_queued()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var beforeTracking = await session.ReadScreenAsync(default);
        await harness.Pty.WriteOutputAsync("\u001b[?1000h");
        _ = await WaitForScreenAsync(
            session,
            snapshot => snapshot.ContentRevision > beforeTracking.ContentRevision);

        harness.Pty.PauseInputWrites();
        var blockedWrite = session.WriteAsync("blocker", default).AsTask();
        await harness.Pty.WaitForInputWriteAttemptAsync();
        var boundSnapshot = await session.ReadScreenAsync(default);
        var mouse = session.SendMouseAtContentRevisionAsync(
            new TerminalMouseInput(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                Column: 0,
                Row: 0),
            boundSnapshot.ContentRevision,
            default).AsTask();
        Assert.False(mouse.IsCompleted);

        await harness.Pty.WriteOutputAsync("changed while mouse was queued");
        _ = await WaitForScreenAsync(
            session,
            snapshot => snapshot.ContentRevision > boundSnapshot.ContentRevision);
        harness.Pty.ResumeInputWrites();

        await blockedWrite.WaitAsync(TimeSpan.FromSeconds(3));
        var outcome = await mouse.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(
            TerminalRevisionBoundMouseOutcome.ContentRevisionChanged,
            outcome);
        Assert.Equal("blocker", harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Force_shutdown_cancels_in_flight_input_and_kills_the_process_once()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.PauseInputWrites();
        var write = session.WriteAsync("blocked", default).AsTask();
        await harness.Pty.WaitForInputWriteAttemptAsync();

        var outcome = await session.CloseAsync(PanelCloseMode.Force, default);

        Assert.Equal(PanelCloseOutcome.ForceTerminated, outcome);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => write.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, harness.Pty.KillCount);
        Assert.Equal(SessionLifecycle.Closed, (await session.SnapshotAsync(default)).Lifecycle);
    }

    [Fact]
    public async Task Shutdown_waits_until_owned_child_is_reaped_after_streams_close()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.DelayExit = true;
        var closing = session.CloseAsync(PanelCloseMode.Force, default).AsTask();
        Assert.False(closing.IsCompleted);
        harness.Pty.Exit(137);
        Assert.Equal(PanelCloseOutcome.ForceTerminated, await closing.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Shutdown_failure_still_releases_native_state_and_remains_observable()
    {
        var harness = await CreateAsync();
        var session = harness.Session;
        harness.Pty.DisposeFailure = new IOException("injected PTY dispose failure");

        var failure = await Assert.ThrowsAsync<IOException>(
            () => session.CloseAsync(PanelCloseMode.Force, default).AsTask());

        Assert.Equal("injected PTY dispose failure", failure.Message);
        Assert.True(ReadPrivateField<SafeHandle>(session, "_terminal").IsClosed);
        Assert.False(ReadPrivateField<GCHandle>(session, "_selfHandle").IsAllocated);

        var repeated = await Assert.ThrowsAsync<IOException>(
            () => session.DisposeAsync().AsTask());
        Assert.Equal(failure.Message, repeated.Message);
    }

    [Fact]
    public async Task Real_portable_pty_and_staged_vt_library_run_a_bounded_command()
    {
        _ = GhosttyVtTestRuntime.RequireStagedRuntime();
        var isWindows = OperatingSystem.IsWindows();
        var executable = isWindows
            ? Environment.GetEnvironmentVariable("COMSPEC")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : "/bin/sh";
        var arguments = isWindows
            ? new[] { "/d", "/c", "echo GHOSTTY_VT_SMOKE" }
            : ["-c", "printf 'GHOSTTY_VT_SMOKE\\n'"];
        var factory = new GhosttyVtTerminalSessionFactory();
        await using var session = await factory.CreateAsync(
            SessionId.New(),
            new TerminalLaunchRequest(Environment.CurrentDirectory, executable, arguments),
            default);

        var screen = await WaitForScreenAsync(
            session,
            snapshot => snapshot.PlainText.Contains("GHOSTTY_VT_SMOKE", StringComparison.Ordinal));

        Assert.Contains("GHOSTTY_VT_SMOKE", screen.PlainText, StringComparison.Ordinal);
        await WaitUntilAsync(async () =>
            (await session.SnapshotAsync(default)).Lifecycle == SessionLifecycle.Closed);
    }

    [Fact]
    public async Task Remote_screen_can_suggest_idle_but_cannot_close_or_spoof_local_exit_status()
    {
        var harness = await CreateAsync(new TerminalLaunchRequest(
            Environment.CurrentDirectory,
            connectionMetadata: new TerminalConnectionMetadata("SSH: user@host:22", null)));
        await using var session = harness.Session;
        await harness.Pty.WriteOutputAsync(
            "WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!\r\n"
            + "Permission denied (publickey).\r\nuser@host:~$ ");
        _ = await WaitForScreenAsync(session, screen => screen.PlainText.Contains("user@host:~$", StringComparison.Ordinal));

        var beforeExit = await session.SnapshotAsync(default);
        Assert.Equal(SessionLifecycle.Active, beforeExit.Lifecycle);
        Assert.False(beforeExit.HasActiveWork); // Deliberately advisory prompt-shape UX, not authorization.
        Assert.Equal(0, harness.Pty.KillCount);
        harness.Pty.Exit(255);
        var afterExit = await session.SnapshotAsync(default);
        Assert.Equal(SessionLifecycle.Closed, afterExit.Lifecycle);
        Assert.Equal("The OpenSSH process exited with code 255.", afterExit.StatusDetail);
        var screenAfterExit = await session.ReadScreenAsync(default);
        Assert.Contains("HOST IDENTIFICATION HAS CHANGED", screenAfterExit.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealOpenSshProcessExitReportsLocalExitCodeWithoutEndpointText()
    {
        _ = GhosttyVtTestRuntime.RequireStagedRuntime();
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/ssh"))
        {
            return;
        }

        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            port: 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var factory = new GhosttyVtTerminalSessionFactory();
        await using var session = await factory.CreateAsync(
            SessionId.New(),
            new TerminalLaunchRequest(
                Environment.CurrentDirectory,
                "/usr/bin/ssh",
                [
                    "-o", "BatchMode=yes",
                    "-o", "ConnectTimeout=2",
                    "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "127.0.0.1",
                ],
                connectionMetadata: new TerminalConnectionMetadata(
                    $"SSH: root@private-endpoint:{port}",
                    initialWorkingDirectory: null)),
            default);

        await WaitUntilAsync(async () =>
            (await session.SnapshotAsync(default)).Lifecycle == SessionLifecycle.Closed);
        var snapshot = await session.SnapshotAsync(default);

        Assert.Equal("The OpenSSH process exited with code 255.", snapshot.StatusDetail);
        Assert.DoesNotContain("127.0.0.1", snapshot.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(port.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initial_command_is_typed_into_the_pty_before_any_user_input()
    {
        _ = GhosttyVtTestRuntime.RequireStagedRuntime();
        var ptyFactory = new FakePortablePtyFactory();
        var factory = new GhosttyVtTerminalSessionFactory(ptyFactory);
        await using var session = await factory.CreateAsync(
            SessionId.New(),
            new TerminalLaunchRequest(
                Environment.CurrentDirectory,
                initialCommand: "htop"),
            default);

        await session.WriteAsync("q", default);

        Assert.StartsWith("htop\n", ptyFactory.Connection.WrittenText, StringComparison.Ordinal);
        Assert.EndsWith("q", ptyFactory.Connection.WrittenText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TerminalPasteSafetyPolicy.ProtectUnsafe, false)]
    [InlineData(TerminalPasteSafetyPolicy.ProtectUnsafeIncludingBracketed, false)]
    [InlineData(TerminalPasteSafetyPolicy.AllowUnsafe, false)]
    [InlineData(TerminalPasteSafetyPolicy.ProtectUnsafe, true)]
    [InlineData(TerminalPasteSafetyPolicy.ProtectUnsafeIncludingBracketed, true)]
    [InlineData(TerminalPasteSafetyPolicy.AllowUnsafe, true)]
    public async Task Legacy_paste_policies_do_not_block_multiline_input(
        TerminalPasteSafetyPolicy legacyPolicy,
        bool bracketed)
    {
        var harness = await CreateAsync(new TerminalLaunchRequest(
            Environment.CurrentDirectory,
            renderProfile: new TerminalRenderProfileSnapshot(
                13,
                TerminalCursorStyle.Block,
                cursorBlink: false,
                scrollbackLines: 10_000,
                TerminalPalette.GhostShellDark,
                clipboardPolicy: new TerminalClipboardPolicy(
                    TerminalClipboardAccess.Ask,
                    TerminalClipboardAccess.Allow,
                    legacyPolicy))));
        await using var session = harness.Session;
        if (bracketed)
        {
            await harness.Pty.WriteOutputAsync("\u001b[?2004hREADY");
            _ = await WaitForScreenAsync(session, snapshot => snapshot.IsBracketedPasteEnabled);
        }

        var result = await session.PasteAsync(
            new TerminalPasteInput("first\n\tsecond"),
            default);

        Assert.Equal(TerminalPasteResult.Completed(bracketed), result);
        Assert.Equal(
            bracketed ? "\u001b[200~first\n\tsecond\u001b[201~" : "first\r\tsecond",
            harness.Pty.WrittenText);
        Assert.Equal(1, harness.Pty.InputWriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paste_neutralizes_editing_controls_and_embedded_bracket_terminators(bool bracketed)
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        if (bracketed)
        {
            await harness.Pty.WriteOutputAsync("\u001b[?2004hREADY");
            _ = await WaitForScreenAsync(session, snapshot => snapshot.IsBracketedPasteEnabled);
        }

        const string text = "a\u0000\u0003\u0004\u0005\u0008\u000f\u0011\u0012\u0013\u0015\u0016\u0017\u001a\u001b\u001c\u007fb\u001b[201~tail";
        var result = await session.PasteAsync(new TerminalPasteInput(text), default);
        var neutralized = "a" + new string(' ', 16) + "b [201~tail";

        Assert.Equal(TerminalPasteResult.Completed(bracketed), result);
        Assert.Equal(
            bracketed ? "\u001b[200~" + neutralized + "\u001b[201~" : neutralized,
            harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Submit_text_delivers_paste_and_enter_in_one_pty_write()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        var result = await session.SubmitTextAsync(
            new TerminalPasteInput("printf ATOMIC"),
            default);

        Assert.Equal(
            TerminalPasteResult.Completed(bracketed: false),
            result);
        Assert.Equal("printf ATOMIC\r", harness.Pty.WrittenText);
        Assert.Equal(1, harness.Pty.InputWriteCount);
    }

    [Fact]
    public async Task Repeated_special_key_is_delivered_in_one_pty_write()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        await session.SendKeyAsync(
            new TerminalKeyStroke(
                TerminalKey.Backspace,
                TerminalKeyModifiers.None),
            default);
        var encodedOnce = harness.Pty.WrittenText;
        await session.SendKeyAsync(
            new TerminalKeyStroke(
                TerminalKey.Backspace,
                TerminalKeyModifiers.None,
                RepeatCount: 12),
            default);

        Assert.NotEmpty(encodedOnce);
        Assert.Equal(
            encodedOnce + string.Concat(Enumerable.Repeat(encodedOnce, 12)),
            harness.Pty.WrittenText);
        Assert.Equal(2, harness.Pty.InputWriteCount);
    }

    [Fact]
    public async Task Screen_input_internal_read_then_diff_preserves_agent_observation_baseline()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await harness.Pty.WriteOutputAsync("before");
        _ = await WaitForScreenAsync(
            session,
            snapshot => snapshot.PlainText.Contains(
                "before",
                StringComparison.Ordinal));
        var baseline = await session.ObserveScreenAsync(default);

        await session.SendKeyAsync(
            new TerminalKeyStroke(TerminalKey.Backspace),
            default);
        await harness.Pty.WriteOutputAsync("\rAFTER");
        var internalRead = await WaitForScreenAsync(
            session,
            snapshot => snapshot.PlainText.Contains(
                "AFTER",
                StringComparison.Ordinal));
        Assert.True(internalRead.ContentRevision > baseline.ContentRevision);
        var diff = await session.ReadScreenDiffAsync(
            new TerminalScreenDiffInput(
                baseline.ContentRevision,
                MaximumRowCount: 24),
            default);
        var stale = await session.ReadScreenDiffAsync(
            new TerminalScreenDiffInput(
                baseline.ContentRevision,
                MaximumRowCount: 24),
            default);

        Assert.True(diff.BaselineAvailable);
        Assert.Contains(
            diff.ChangedRows,
            row => row.Text.Contains("AFTER", StringComparison.Ordinal));
        Assert.False(stale.BaselineAvailable);
        Assert.Empty(stale.ChangedRows);
    }

    private static async Task<GhosttyVtHarness> CreateAsync(
        TerminalLaunchRequest? launch = null)
    {
        _ = GhosttyVtTestRuntime.RequireStagedRuntime();
        var ptyFactory = new FakePortablePtyFactory();
        var factory = new GhosttyVtTerminalSessionFactory(ptyFactory);
        var session = await factory.CreateAsync(
            SessionId.New(),
            launch ?? new TerminalLaunchRequest(Environment.CurrentDirectory),
            default);
        return new GhosttyVtHarness(
            Assert.IsType<GhosttyVtTerminalSession>(session),
            ptyFactory.Connection);
    }

    private static GhosttyVtHarness CreateWithShellIntegrationSupport()
    {
        _ = GhosttyVtTestRuntime.RequireStagedRuntime();
        var launch = new TerminalLaunchRequest(Environment.CurrentDirectory);
        var pty = new FakePortablePtyConnection();
        var session = new GhosttyVtTerminalSession(
            SessionId.New(),
            launch,
            pty,
            initialColumns: 80,
            initialRows: 24,
            shellIntegrationApplied: true);
        return new GhosttyVtHarness(session, pty);
    }

    private static TerminalPhysicalKeyEvent PhysicalKey(
        TerminalKeyAction action,
        string text = "a") =>
        new(
            TerminalPhysicalKey.A,
            "A",
            text,
            TerminalKeyModifiers.None,
            TerminalKeyModifiers.None,
            action,
            'a');

    private static T ReadPrivateField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field {name}.");
        return Assert.IsAssignableFrom<T>(field.GetValue(instance));
    }

    private static async Task<TerminalRenderFrame> WaitForFrameAsync(
        GhosttyVtTerminalSession session,
        Func<TerminalRenderFrame, bool> condition)
    {
        TerminalRenderFrame? last = null;
        await WaitUntilAsync(async () =>
        {
            last = await session.ReadRenderFrameAsync(default);
            return condition(last);
        });
        return last!;
    }

    private static async Task<TerminalScreenSnapshot> WaitForScreenAsync(
        ITerminalPanelSession session,
        Func<TerminalScreenSnapshot, bool> condition)
    {
        TerminalScreenSnapshot? last = null;
        await WaitUntilAsync(async () =>
        {
            last = await session.ReadScreenAsync(default);
            return condition(last);
        });
        return last!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(3))
            {
                throw new TimeoutException("The libghostty-vt terminal did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private sealed record GhosttyVtHarness(
        GhosttyVtTerminalSession Session,
        FakePortablePtyConnection Pty);
}
