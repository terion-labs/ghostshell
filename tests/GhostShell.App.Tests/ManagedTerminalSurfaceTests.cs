using Avalonia;
using Avalonia.Automation;
using Avalonia.Input;
using GhostShell.App.Controls;
using GhostShell.Application;
using GhostShell.Core;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;
using CoreKeyModifiers = GhostShell.Core.KeyModifiers;

namespace GhostShell.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class ManagedTerminalSurfaceTests
{
    [Fact]
    public async Task Sends_typed_special_keys_modified_text_and_ime_text()
    {
        var sink = new RecordingInputSink();
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(),
        };
        surface.Measure(new Size(800, 400));
        surface.Arrange(new Rect(0, 0, 800, 400));
        surface.UpdateSnapshot(Snapshot(mouseTracking: false, columns: 96, rows: 24));
        var ime = new TerminalTextInputMethodClient(surface);
        ime.SetPreeditText("にほ", 1);
        Assert.Equal("にほ", surface.PreeditText);
        var baseCursor = surface.Metrics.CellBounds(0, 0);
        Assert.Equal(
            new Rect(
                baseCursor.X + surface.Metrics.CellWidth,
                baseCursor.Y,
                1,
                baseCursor.Height),
            ime.CursorRectangle);

        Assert.True(await surface.SubmitKeyAsync(
            Key.Up,
            AvaloniaKeyModifiers.Control | AvaloniaKeyModifiers.Shift));
        Assert.True(await surface.SubmitKeyAsync(
            Key.C,
            AvaloniaKeyModifiers.Control,
            "c"));
        Assert.False(await surface.SubmitKeyAsync(
            Key.A,
            AvaloniaKeyModifiers.Shift,
            "A"));
        Assert.False(await surface.SubmitKeyAsync(
            Key.Q,
            AvaloniaKeyModifiers.Control | AvaloniaKeyModifiers.Alt,
            "@"));
        await surface.SubmitTextInputAsync("にほん語");

        var key = Assert.Single(sink.Keys);
        Assert.Equal(TerminalKey.Up, key.Key);
        Assert.Equal(
            TerminalKeyModifiers.Control | TerminalKeyModifiers.Shift,
            key.Modifiers);
        Assert.Equal(["\u0003", "にほん語"], sink.Text);
        Assert.Empty(surface.PreeditText);
    }

    [Fact]
    public async Task Physical_key_events_preserve_layout_text_actions_and_consumed_modifiers()
    {
        var sink = new RecordingInputSink();
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(),
        };

        Assert.True(await surface.SubmitPhysicalKeyAsync(
            Key.A,
            PhysicalKey.A,
            AvaloniaKeyModifiers.Shift,
            "A",
            TerminalKeyAction.Press));
        Assert.True(await surface.SubmitPhysicalKeyAsync(
            Key.A,
            PhysicalKey.A,
            AvaloniaKeyModifiers.Shift,
            "A",
            TerminalKeyAction.Repeat));
        Assert.True(await surface.SubmitPhysicalKeyAsync(
            Key.A,
            PhysicalKey.A,
            AvaloniaKeyModifiers.None,
            keySymbol: null,
            action: TerminalKeyAction.Release));

        Assert.Equal(
            [TerminalKeyAction.Press, TerminalKeyAction.Repeat, TerminalKeyAction.Release],
            sink.PhysicalKeys.Select(keyEvent => keyEvent.Action));
        var press = sink.PhysicalKeys[0];
        Assert.Equal(TerminalPhysicalKey.A, press.PhysicalKey);
        Assert.Equal("A", press.LogicalKey);
        Assert.Equal("A", press.Text);
        Assert.Equal((uint)'a', press.UnshiftedCodepoint);
        Assert.Equal(TerminalKeyModifiers.Shift, press.Modifiers);
        Assert.Equal(TerminalKeyModifiers.Shift, press.ConsumedModifiers);
        Assert.Equal(string.Empty, sink.PhysicalKeys[^1].Text);
    }

    [Fact]
    public void Physical_key_translation_matches_terminal_text_semantics()
    {
        var shiftedPunctuation = ManagedTerminalInput.CreatePhysicalKeyEvent(
            Key.OemPlus,
            PhysicalKey.Equal,
            AvaloniaKeyModifiers.Shift,
            "+",
            TerminalKeyAction.Press,
            isComposing: false);
        var optionText = ManagedTerminalInput.CreatePhysicalKeyEvent(
            Key.A,
            PhysicalKey.A,
            AvaloniaKeyModifiers.Alt,
            "å",
            TerminalKeyAction.Press,
            isComposing: false);
        var plainAlt = ManagedTerminalInput.CreatePhysicalKeyEvent(
            Key.A,
            PhysicalKey.A,
            AvaloniaKeyModifiers.Alt,
            "a",
            TerminalKeyAction.Press,
            isComposing: false);
        var controlText = ManagedTerminalInput.CreatePhysicalKeyEvent(
            Key.A,
            PhysicalKey.A,
            AvaloniaKeyModifiers.Control,
            "\u0001",
            TerminalKeyAction.Press,
            isComposing: false);
        var altGr = ManagedTerminalInput.CreatePhysicalKeyEvent(
            Key.Q,
            PhysicalKey.Q,
            AvaloniaKeyModifiers.Control | AvaloniaKeyModifiers.Alt,
            "@",
            TerminalKeyAction.Press,
            isComposing: false);
        var shiftedCyrillic = ManagedTerminalInput.CreatePhysicalKeyEvent(
            Key.Z,
            PhysicalKey.Z,
            AvaloniaKeyModifiers.Shift,
            "Я",
            TerminalKeyAction.Press,
            isComposing: false);

        Assert.Equal((uint)'=', shiftedPunctuation.UnshiftedCodepoint);
        Assert.Equal(TerminalKeyModifiers.Shift, shiftedPunctuation.ConsumedModifiers);
        Assert.Equal(TerminalKeyModifiers.Alt, optionText.ConsumedModifiers);
        Assert.Equal(TerminalKeyModifiers.None, plainAlt.ConsumedModifiers);
        Assert.Equal("a", controlText.Text);
        Assert.Equal(TerminalKeyModifiers.None, controlText.ConsumedModifiers);
        Assert.Equal(
            TerminalKeyModifiers.Control | TerminalKeyModifiers.Alt,
            altGr.ConsumedModifiers);
        Assert.Equal((uint)'я', shiftedCyrillic.UnshiftedCodepoint);
        Assert.Equal(TerminalKeyModifiers.Shift, shiftedCyrillic.ConsumedModifiers);
    }

    [Theory]
    [InlineData(PhysicalKey.MetaLeft)]
    [InlineData(PhysicalKey.MetaRight)]
    [InlineData(PhysicalKey.ControlLeft)]
    [InlineData(PhysicalKey.ShiftRight)]
    public void Modifier_only_keys_stay_in_the_desktop_shortcut_layer(PhysicalKey key) =>
        Assert.True(ManagedTerminalInput.IsModifierOnly(key));

    [Fact]
    public void Modified_character_key_still_reaches_the_terminal_or_keymap() =>
        Assert.False(ManagedTerminalInput.IsModifierOnly(PhysicalKey.C));

    [Fact]
    public async Task SelectedKeymapControlsCopyAndPasteInsteadOfPlatformHardCoding()
    {
        var sink = new RecordingInputSink
        {
            SelectionText = new TerminalSelectionText("selected", true, false),
        };
        var clipboard = new RecordingClipboard { Text = "paste from custom binding" };
        var keymap = Keymap(
            Binding(BuiltInCommands.Copy, "X", CoreKeyModifiers.Meta),
            Binding(BuiltInCommands.Paste, "P", CoreKeyModifiers.Meta));
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Clipboard = clipboard,
            Profile = Profile(),
            Keymap = TerminalKeymapSnapshot.FromProfile(keymap),
        };

        var oldPlatformCopy = await surface.DispatchKeymapShortcutAsync(
            Key.C,
            AvaloniaKeyModifiers.Control | AvaloniaKeyModifiers.Shift);
        var copy = await surface.DispatchKeymapShortcutAsync(Key.X, AvaloniaKeyModifiers.Meta);
        var paste = await surface.DispatchKeymapShortcutAsync(Key.P, AvaloniaKeyModifiers.Meta);

        Assert.Equal(TerminalCommandDispatchResult.Outcome.NotMatched, oldPlatformCopy.Status);
        Assert.Equal(TerminalCommandDispatchResult.Outcome.Executed, copy.Status);
        Assert.Equal(BuiltInCommands.Copy, copy.CommandId);
        Assert.Equal("selected", Assert.Single(clipboard.Writes));
        Assert.Equal(TerminalCommandDispatchResult.Outcome.Executed, paste.Status);
        Assert.Equal("paste from custom binding", Assert.Single(sink.Pastes).Text);
    }

    [Fact]
    public async Task CopyFailureExplainsMouseSelectionWithoutMisreportingClipboardPolicy()
    {
        var surface = new ManagedTerminalSurface
        {
            InputSink = new RecordingInputSink(),
            Clipboard = new RecordingClipboard(),
            Profile = Profile(),
            Keymap = TerminalKeymapSnapshot.FromProfile(Keymap(
                Binding(BuiltInCommands.Copy, "C", CoreKeyModifiers.Meta))),
        };
        surface.UpdateSnapshot(Snapshot(mouseTracking: true, columns: 80, rows: 24));

        var result = await surface.DispatchKeymapShortcutAsync(
            Key.C,
            AvaloniaKeyModifiers.Meta);

        Assert.Equal(TerminalCommandDispatchResult.Outcome.Unavailable, result.Status);
        Assert.Equal(
            "No terminal text is selected. Hold Shift while dragging when the running app handles the mouse.",
            result.Message);
        Assert.DoesNotContain("policy", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CanonicalTerminalEditingCommandsSendSemanticControlSequences()
    {
        var sink = new RecordingInputSink();
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(),
            Keymap = TerminalKeymapSnapshot.FromProfile(Keymap(
                Binding(BuiltInCommands.MoveWordLeft, "B", CoreKeyModifiers.Control),
                Binding(BuiltInCommands.DeleteWordForward, "D", CoreKeyModifiers.Control),
                Binding(BuiltInCommands.ClearScreen, "L", CoreKeyModifiers.Control))),
        };

        Assert.Equal(
            TerminalCommandDispatchResult.Outcome.Executed,
            (await surface.DispatchKeymapShortcutAsync(Key.B, AvaloniaKeyModifiers.Control)).Status);
        Assert.Equal(
            TerminalCommandDispatchResult.Outcome.Executed,
            (await surface.DispatchKeymapShortcutAsync(Key.D, AvaloniaKeyModifiers.Control)).Status);
        Assert.Equal(
            TerminalCommandDispatchResult.Outcome.Executed,
            (await surface.DispatchKeymapShortcutAsync(Key.L, AvaloniaKeyModifiers.Control)).Status);

        Assert.Equal(["\u001bb", "\u001bd", "\u000c"], sink.Text);
    }

    [Fact]
    public async Task UnsupportedKeymapCommandsStayTypedAndConsumeTheirShortcut()
    {
        var sink = new RecordingInputSink();
        var futureCommand = new CommandId("terminal.future-action");
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(),
            Keymap = TerminalKeymapSnapshot.FromProfile(Keymap(
                Binding(futureCommand, "U", CoreKeyModifiers.Control))),
        };
        TerminalCommandDispatchResult? observed = null;
        surface.CommandDispatched += (_, result) => observed = result;

        var result = await surface.DispatchKeymapShortcutAsync(
            Key.U,
            AvaloniaKeyModifiers.Control);

        Assert.Equal(TerminalCommandDispatchResult.Outcome.Unsupported, result.Status);
        Assert.Equal(futureCommand, result.CommandId);
        Assert.True(result.ShouldHandle);
        Assert.Same(result, observed);
        Assert.Contains(futureCommand.Value, result.Message, StringComparison.Ordinal);
        Assert.Equal(result.Message, surface.CommandStatusMessage);
        Assert.Empty(sink.Text);
        Assert.Empty(sink.Keys);
    }

    [Fact]
    public async Task MultiStrokeTerminalBindingUsesTheLaunchSnapshotSequence()
    {
        var sink = new RecordingInputSink
        {
            SelectionText = new TerminalSelectionText("sequence", true, false),
        };
        var clipboard = new RecordingClipboard();
        var binding = new CommandBinding(
            BuiltInCommands.Copy,
            KeySequence.Of(
                new KeyStroke("X", CoreKeyModifiers.Control),
                new KeyStroke("C")),
            CommandContext.Terminal);
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Clipboard = clipboard,
            Profile = Profile(),
            Keymap = new TerminalKeymapSnapshot(
                new KeymapProfileId("terminal.sequence"),
                "Sequence",
                [binding]),
        };
        var start = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var prefix = await surface.DispatchKeymapShortcutAsync(
            Key.X,
            AvaloniaKeyModifiers.Control,
            timestamp: start);
        var completed = await surface.DispatchKeymapShortcutAsync(
            Key.C,
            AvaloniaKeyModifiers.None,
            timestamp: start.AddMilliseconds(20));

        Assert.Equal(TerminalCommandDispatchResult.Outcome.Pending, prefix.Status);
        Assert.Equal(TerminalCommandDispatchResult.Outcome.Executed, completed.Status);
        Assert.Equal("sequence", Assert.Single(clipboard.Writes));
    }

    [Fact]
    public async Task RepeatablePrefixAcceptsAnotherBoundSuffixWithinTheTimeout()
    {
        var prefixStroke = new KeyStroke("X", CoreKeyModifiers.Control);
        var binding = new CommandBinding(
            BuiltInCommands.Copy,
            KeySequence.Of(prefixStroke, new KeyStroke("C")),
            CommandContext.Terminal);
        var clipboard = new RecordingClipboard();
        var surface = new ManagedTerminalSurface
        {
            InputSink = new RecordingInputSink
            {
                SelectionText = new TerminalSelectionText("repeatable", true, false),
            },
            Clipboard = clipboard,
            Profile = Profile(),
            Keymap = new TerminalKeymapSnapshot(
                new KeymapProfileId("terminal.repeatable"),
                "Repeatable terminal sequence",
                [binding],
                new PrefixConfiguration(
                    prefixStroke,
                    TimeSpan.FromMilliseconds(750),
                    repeatable: true,
                    FailedSequenceBehavior.DiscardAndShowHint)),
        };
        var start = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var prefix = await surface.DispatchKeymapStrokeAsync(prefixStroke, start);
        var first = await surface.DispatchKeymapStrokeAsync(
            new KeyStroke("C"),
            start.AddMilliseconds(20));
        var repeated = await surface.DispatchKeymapStrokeAsync(
            new KeyStroke("C"),
            start.AddMilliseconds(40));

        Assert.Equal(TerminalCommandDispatchResult.Outcome.Pending, prefix.Status);
        Assert.Equal(TerminalCommandDispatchResult.Outcome.Executed, first.Status);
        Assert.Equal(TerminalCommandDispatchResult.Outcome.Executed, repeated.Status);
        Assert.Equal(["repeatable", "repeatable"], clipboard.Writes);
    }

    [Fact]
    public async Task PassThroughSequenceReplaysTheBufferedPrefixAndFailingSuffixOnce()
    {
        var prefixStroke = new KeyStroke("X", CoreKeyModifiers.Control);
        var binding = new CommandBinding(
            BuiltInCommands.Copy,
            KeySequence.Of(prefixStroke, new KeyStroke("C")),
            CommandContext.Terminal);
        var sink = new RecordingInputSink();
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(),
            Keymap = new TerminalKeymapSnapshot(
                new KeymapProfileId("terminal.pass-through"),
                "Pass-through terminal sequence",
                [binding],
                new PrefixConfiguration(
                    prefixStroke,
                    TimeSpan.FromMilliseconds(750),
                    repeatable: false,
                    FailedSequenceBehavior.PassThrough)),
        };
        var start = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        _ = await surface.DispatchKeymapStrokeAsync(prefixStroke, start);
        var result = await surface.DispatchKeymapStrokeAsync(
            new KeyStroke("Q"),
            start.AddMilliseconds(20));

        Assert.Equal(TerminalCommandDispatchResult.Outcome.PassedThrough, result.Status);
        Assert.True(result.ShouldHandle);
        Assert.Equal(["\u0018", "q"], sink.Text);
        Assert.Empty(sink.Keys);
    }

    [Fact]
    public async Task PendingSequenceIsVisibleToAutomationAndClearsOnTimeout()
    {
        var prefixStroke = new KeyStroke("X", CoreKeyModifiers.Control);
        var binding = new CommandBinding(
            BuiltInCommands.Copy,
            KeySequence.Of(prefixStroke, new KeyStroke("C")),
            CommandContext.Terminal);
        var surface = new ManagedTerminalSurface
        {
            InputSink = new RecordingInputSink(),
            Profile = Profile(),
            Keymap = new TerminalKeymapSnapshot(
                new KeymapProfileId("terminal.timeout"),
                "Expiring terminal sequence",
                [binding],
                new PrefixConfiguration(
                    prefixStroke,
                    TimeSpan.FromMilliseconds(100),
                    repeatable: false,
                    FailedSequenceBehavior.DiscardAndShowHint)),
        };
        var start = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var pending = await surface.DispatchKeymapStrokeAsync(prefixStroke, start);

        Assert.Equal(TerminalCommandDispatchResult.Outcome.Pending, pending.Status);
        Assert.Equal(pending.Message, surface.CommandStatusMessage);
        Assert.Contains(
            pending.Message,
            AutomationProperties.GetItemStatus(surface),
            StringComparison.Ordinal);

        Assert.True(await surface.ExpirePendingKeySequenceAsync(start.AddMilliseconds(101)));
        Assert.Empty(surface.CommandStatusMessage);
        Assert.DoesNotContain(
            pending.Message,
            AutomationProperties.GetItemStatus(surface),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryBuiltInTerminalBindingIsExecutedOrExplicitlyUnavailable()
    {
        var terminalPresets = BuiltInKeymaps.All.Where(profile => profile.Layer == KeymapLayer.Terminal);
        foreach (var preset in terminalPresets)
        {
            foreach (var binding in preset.Bindings)
            {
                var sink = new RecordingInputSink
                {
                    SelectionText = new TerminalSelectionText("copy", true, false),
                };
                var surface = new ManagedTerminalSurface
                {
                    InputSink = sink,
                    Clipboard = new RecordingClipboard { Text = "paste" },
                    Profile = Profile(),
                    Keymap = TerminalKeymapSnapshot.FromProfile(preset),
                };
                surface.UpdateSnapshot(Snapshot(mouseTracking: false, columns: 80, rows: 24));

                TerminalCommandDispatchResult? result = null;
                foreach (var stroke in binding.Sequence.Strokes)
                {
                    result = await surface.DispatchKeymapStrokeAsync(stroke);
                }

                Assert.NotNull(result);
                Assert.Equal(binding.CommandId, result.CommandId);
                Assert.True(
                    result.Status == TerminalCommandDispatchResult.Outcome.Executed,
                    $"{preset.Name}: {binding.CommandId} returned {result.Status}: {result.Message}");
            }
        }
    }

    [Fact]
    public async Task Find_and_clear_scrollback_are_visible_typed_buffer_commands()
    {
        var sink = new RecordingInputSink();
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(),
            Keymap = TerminalKeymapSnapshot.FromProfile(Keymap(
                Binding(BuiltInCommands.Find, "F", CoreKeyModifiers.Control),
                Binding(BuiltInCommands.ClearScrollback, "K", CoreKeyModifiers.Control))),
        };
        surface.UpdateSnapshot(Snapshot(mouseTracking: false, columns: 80, rows: 24));

        var find = await surface.DispatchKeymapShortcutAsync(Key.F, AvaloniaKeyModifiers.Control);
        await surface.SubmitTextInputAsync("needle");
        Assert.True(await surface.HandleFindKeyAsync(Key.Enter, AvaloniaKeyModifiers.None));

        Assert.Equal(TerminalCommandDispatchResult.Outcome.Executed, find.Status);
        Assert.True(surface.IsFindVisible);
        Assert.Equal("needle", surface.FindQuery);
        Assert.Equal(3, sink.Finds.Count);
        Assert.Equal(string.Empty, sink.Finds[0].Query);
        Assert.Equal(new TerminalFindInput("needle", 0), sink.Finds[1]);
        Assert.Equal(new TerminalFindInput("needle", 1), sink.Finds[2]);
        Assert.Contains("Match 2 of 3", surface.FindStatusMessage, StringComparison.Ordinal);
        Assert.Contains("find query 'needle'", AutomationProperties.GetItemStatus(surface), StringComparison.Ordinal);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(surface));
        Assert.Empty(sink.Text);

        Assert.True(await surface.HandleFindKeyAsync(Key.Escape, AvaloniaKeyModifiers.None));
        Assert.False(surface.IsFindVisible);
        Assert.Equal(string.Empty, sink.Finds[^1].Query);

        var clear = await surface.DispatchKeymapShortcutAsync(Key.K, AvaloniaKeyModifiers.Control);
        Assert.Equal(TerminalCommandDispatchResult.Outcome.Executed, clear.Status);
        Assert.Equal(1, sink.ClearScrollbackCount);
    }

    [Fact]
    public async Task Unsupported_buffer_commands_are_consumed_and_explained()
    {
        var sink = new RecordingInputSink
        {
            SupportsFind = false,
            SupportsClearScrollback = false,
        };
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(),
            Keymap = TerminalKeymapSnapshot.FromProfile(Keymap(
                Binding(BuiltInCommands.Find, "F", CoreKeyModifiers.Control),
                Binding(BuiltInCommands.ClearScrollback, "K", CoreKeyModifiers.Control))),
        };

        var find = await surface.DispatchKeymapShortcutAsync(Key.F, AvaloniaKeyModifiers.Control);
        var clear = await surface.DispatchKeymapShortcutAsync(Key.K, AvaloniaKeyModifiers.Control);

        Assert.Equal(TerminalCommandDispatchResult.Outcome.Unavailable, find.Status);
        Assert.False(surface.IsFindVisible);
        Assert.Equal(TerminalCommandDispatchResult.Outcome.Unavailable, clear.Status);
        Assert.Equal(clear.Message, surface.CommandStatusMessage);
    }

    [Fact]
    public async Task Mouse_events_are_cell_exact_and_only_sent_in_mouse_mode()
    {
        var sink = new RecordingInputSink();
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(fontSize: 10),
        };
        surface.Measure(new Size(644, 260));
        surface.Arrange(new Rect(0, 0, 644, 260));
        var viewport = surface.CurrentViewport(1.5);
        Assert.Equal(100, viewport.Columns);
        Assert.Equal(20, viewport.Rows);
        Assert.Equal(1.5, viewport.RenderScale);

        surface.UpdateSnapshot(Snapshot(mouseTracking: false, columns: 100, rows: 20));
        Assert.False(await surface.SubmitMouseAsync(
            TerminalMouseButton.Left,
            TerminalMouseEventKind.Down,
            new Point(30, 30),
            AvaloniaKeyModifiers.None));
        Assert.Empty(sink.Mouse);

        surface.UpdateSnapshot(Snapshot(
            mouseTracking: true,
            columns: 100,
            rows: 20,
            revision: 1));
        Assert.True(await surface.SubmitMouseAsync(
            TerminalMouseButton.Left,
            TerminalMouseEventKind.Down,
            new Point(12 + 2.2 * 6.2, 10 + 3.2 * 12),
            AvaloniaKeyModifiers.Alt));
        Assert.False(await surface.SubmitMouseAsync(
            TerminalMouseButton.Left,
            TerminalMouseEventKind.Drag,
            new Point(12 + 2.8 * 6.2, 10 + 3.8 * 12),
            AvaloniaKeyModifiers.Alt));

        var mouse = Assert.Single(sink.Mouse);
        Assert.Equal(2, mouse.Column);
        Assert.Equal(3, mouse.Row);
        Assert.Equal(TerminalKeyModifiers.Alt, mouse.Modifiers);
    }

    [Fact]
    public async Task Multiline_paste_is_sent_immediately()
    {
        var sink = new RecordingInputSink();
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(),
        };

        var result = await surface.PasteTextAsync("first\nsecond");

        Assert.True(result.Sent);
        Assert.False(result.RequiresConfirmation);
        var paste = Assert.Single(sink.Pastes);
        Assert.Equal("first\nsecond", paste.Text);
    }

    [Fact]
    public async Task Input_is_suppressed_until_the_host_marks_the_surface_ready()
    {
        var sink = new RecordingInputSink();
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(),
        };
        surface.UpdateSnapshot(Snapshot(mouseTracking: true, columns: 80, rows: 24));
        surface.SetInputReady(false);

        Assert.False(await surface.SubmitKeyAsync(Key.Up, AvaloniaKeyModifiers.None));
        await surface.SubmitTextInputAsync("not sent");
        Assert.False(await surface.SubmitMouseAsync(
            TerminalMouseButton.Left,
            TerminalMouseEventKind.Down,
            new Point(12, 10),
            AvaloniaKeyModifiers.None));
        Assert.Empty(sink.Keys);
        Assert.Empty(sink.Text);
        Assert.Empty(sink.Mouse);

        surface.SetInputReady(true);
        Assert.True(await surface.SubmitKeyAsync(Key.Up, AvaloniaKeyModifiers.None));
        Assert.Single(sink.Keys);
    }

    [Fact]
    public void Unarranged_surface_uses_a_safe_initial_terminal_grid()
    {
        var surface = new ManagedTerminalSurface
        {
            Profile = Profile(fontSize: 16),
        };

        var viewport = surface.CurrentViewport(2);

        Assert.Equal(80, viewport.Columns);
        Assert.Equal(24, viewport.Rows);
        Assert.Equal(2, viewport.RenderScale);
        Assert.Equal(surface.Metrics.CellWidth * 80, viewport.LogicalWidth);
        Assert.Equal(surface.Metrics.CellHeight * 24, viewport.LogicalHeight);
    }

    [Fact]
    public void Structured_layout_preserves_wide_and_combining_cells_and_resolves_colors()
    {
        var palette = TerminalPalette.GhostShellDark;
        var row = new TerminalScreenRow(
            0,
            [
                new TerminalScreenCell(
                    "R",
                    1,
                    new TerminalCellColor(TerminalColorMode.Indexed, 196),
                    TerminalCellColor.Default,
                    Hyperlink: "https://example.test",
                    IsSelected: true),
                new TerminalScreenCell(
                    "界",
                    2,
                    new TerminalCellColor(TerminalColorMode.Rgb, 0x112233),
                    TerminalCellColor.Default),
                new TerminalScreenCell(
                    string.Empty,
                    0,
                    TerminalCellColor.Default,
                    TerminalCellColor.Default),
                new TerminalScreenCell(
                    "e\u0301",
                    1,
                    TerminalCellColor.Default,
                    new TerminalCellColor(TerminalColorMode.Rgb, 0x445566),
                    TerminalCellStyle.Inverse),
            ]);
        var snapshot = new TerminalScreenSnapshot(
            "R界e\u0301",
            0,
            4,
            1,
            8,
            false,
            null,
            DateTimeOffset.UtcNow,
            StructuredRows: [row]);
        var metrics = TerminalCellMetrics.Measure(new Size(200, 80), Profile());

        var frame = TerminalRenderLayout.Create(snapshot, Profile(), metrics);

        Assert.Equal(3, frame.Cells.Count);
        Assert.Equal((1, 2, "界"),
            (frame.Cells[1].Column, frame.Cells[1].Width, frame.Cells[1].Text));
        Assert.Equal((3, 1, "e\u0301"),
            (frame.Cells[2].Column, frame.Cells[2].Width, frame.Cells[2].Text));
        Assert.Equal(Avalonia.Media.Color.FromRgb(0x44, 0x55, 0x66), frame.Cells[2].Foreground);
        Assert.Equal(Avalonia.Media.Color.FromRgb(0x11, 0x22, 0x33), frame.Cells[1].Foreground);
        Assert.Equal(
            Avalonia.Media.Color.FromRgb(
                palette.Background.Red,
                palette.Background.Green,
                palette.Background.Blue),
            frame.Cells[1].Background);
        Assert.True(frame.Cells[0].IsSelected);
        Assert.False(frame.Cells[0].UsesDefaultBackground);
        Assert.True(frame.Cells[1].UsesDefaultBackground);
        Assert.False(frame.Cells[2].UsesDefaultBackground);
        Assert.Equal("https://example.test", frame.Cells[0].Hyperlink);
        Assert.Equal(
            Avalonia.Media.Color.FromRgb(
                palette.SelectionBackground.Red,
                palette.SelectionBackground.Green,
                palette.SelectionBackground.Blue),
            frame.Cells[0].Background);
        Assert.Equal(
            Avalonia.Media.Color.FromRgb(
                palette.Foreground.Red,
                palette.Foreground.Green,
                palette.Foreground.Blue),
            frame.Cells[0].Foreground);
    }

    [Fact]
    public void Rich_layout_preserves_terminal_cursor_underlines_and_kitty_layers()
    {
        var palette = TerminalPalette.GhostShellDark;
        var underlineColor = new TerminalCellColor(TerminalColorMode.Rgb, 0x12AB34);
        var imageKey = new TerminalKittyImageKey(7, 11);
        var image = new TerminalKittyImageContent(
            imageKey,
            ImageNumber: 4,
            PixelWidth: 1,
            PixelHeight: 1,
            TerminalKittyImagePixelFormat.Rgba,
            new byte[] { 1, 2, 3, 255 });
        var placement = new TerminalKittyPlacement(
            imageKey,
            PlacementId: 9,
            IsVirtual: false,
            ZIndex: -1,
            PixelOffsetX: 0,
            PixelOffsetY: 0,
            new TerminalKittySourceRectangle(0, 0, 1, 1),
            new TerminalKittyPlacementGeometry(0, 0, 1, 1, 1, 1));
        var cells = new TerminalRenderCell[]
        {
            new(
                "A",
                TerminalRenderCellWidth.Narrow,
                TerminalCellColor.Default,
                TerminalCellColor.Default,
                TerminalRenderCellStyle.Bold | TerminalRenderCellStyle.Italic,
                TerminalUnderlineKind.Curly,
                underlineColor,
                Hyperlink: "https://example.test"),
            new(
                "界",
                TerminalRenderCellWidth.Wide,
                new TerminalCellColor(TerminalColorMode.Rgb, 0x112233),
                TerminalCellColor.Default),
            new(
                string.Empty,
                TerminalRenderCellWidth.SpacerTail,
                TerminalCellColor.Default,
                TerminalCellColor.Default),
            new(
                "B",
                TerminalRenderCellWidth.Narrow,
                TerminalCellColor.Default,
                TerminalCellColor.Default),
        };
        var source = new TerminalRenderFrame(
            Revision: 5,
            Rows: 1,
            Columns: 4,
            [new TerminalRenderRow(0, cells)],
            new TerminalRenderCursor(
                TerminalCursorVisualStyle.Underline,
                IsVisible: true,
                IsBlinking: true,
                IsPasswordInput: false,
                ViewportRow: 0,
                ViewportColumn: 2,
                IsWideCharacterTail: true,
                Color: new TerminalCellColor(TerminalColorMode.Rgb, 0x445566)),
            new TerminalRenderDelta(TerminalRenderDamageKind.Full),
            new TerminalKittyGraphicsFrame(3, [placement], [image]));
        var metrics = TerminalCellMetrics.Measure(new Size(200, 80), Profile());

        var frame = TerminalRenderLayout.Create(source, Profile(), metrics);

        Assert.Equal(4, frame.Cells.Count);
        Assert.Equal(2, frame.Cells[1].Width);
        Assert.Empty(frame.Cells[2].Text);
        Assert.Equal(TerminalUnderlineKind.Curly, frame.Cells[0].Underline);
        Assert.Equal(Avalonia.Media.Color.FromRgb(0x12, 0xAB, 0x34), frame.Cells[0].UnderlineColor);
        Assert.True(frame.Cells[0].Style.HasFlag(TerminalRenderCellStyle.Bold));
        Assert.True(frame.Cells[0].Style.HasFlag(TerminalRenderCellStyle.Italic));
        Assert.True(frame.Cells[0].UsesDefaultBackground);
        Assert.True(frame.Cells[1].UsesDefaultBackground);
        Assert.Equal((0, 1, 2), (frame.Cursor.Row, frame.Cursor.Column, frame.Cursor.Width));
        Assert.Equal(TerminalCursorVisualStyle.Underline, frame.Cursor.VisualStyle);
        Assert.True(frame.Cursor.IsBlinking);
        Assert.Equal(Avalonia.Media.Color.FromRgb(0x44, 0x55, 0x66), frame.Cursor.Color);
        Assert.Same(source.KittyGraphics, frame.KittyGraphics);
        Assert.Equal(TerminalKittyPlacementLayer.BelowText, placement.Layer);
        Assert.Equal(
            Avalonia.Media.Color.FromRgb(
                palette.Background.Red,
                palette.Background.Green,
                palette.Background.Blue),
            frame.Cells[1].Background);
    }

    [Fact]
    public void Background_transparency_is_limited_to_unmodified_default_cells()
    {
        var cells = new TerminalRenderCell[]
        {
            new(
                "D",
                TerminalRenderCellWidth.Narrow,
                TerminalCellColor.Default,
                TerminalCellColor.Default),
            new(
                "A",
                TerminalRenderCellWidth.Narrow,
                TerminalCellColor.Default,
                new TerminalCellColor(TerminalColorMode.Rgb, 0x112233)),
            new(
                "I",
                TerminalRenderCellWidth.Narrow,
                TerminalCellColor.Default,
                TerminalCellColor.Default,
                TerminalRenderCellStyle.Inverse),
            new(
                "S",
                TerminalRenderCellWidth.Narrow,
                TerminalCellColor.Default,
                TerminalCellColor.Default,
                IsSelected: true),
        };
        var source = new TerminalRenderFrame(
            Revision: 1,
            Rows: 1,
            Columns: cells.Length,
            [new TerminalRenderRow(0, cells)],
            new TerminalRenderCursor(
                TerminalCursorVisualStyle.Block,
                IsVisible: false,
                IsBlinking: false,
                IsPasswordInput: false),
            new TerminalRenderDelta(TerminalRenderDamageKind.Full),
            TerminalKittyGraphicsFrame.Empty);
        var metrics = TerminalCellMetrics.Measure(new Size(240, 80), Profile());

        var frame = TerminalRenderLayout.Create(source, Profile(), metrics);

        Assert.Equal(
            [true, false, false, false],
            frame.Cells.Select(cell => cell.UsesDefaultBackground));
    }

    [Fact]
    public async Task Local_scroll_and_selection_use_typed_operations_and_never_emit_remote_mouse()
    {
        var sink = new RecordingInputSink();
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(fontSize: 10),
        };
        surface.Measure(new Size(400, 160));
        surface.Arrange(new Rect(0, 0, 400, 160));
        surface.UpdateSnapshot(Snapshot(mouseTracking: false, columns: 60, rows: 10));

        Assert.True(await surface.ScrollViewportAsync(-3));
        Assert.True(await surface.SubmitSelectionAsync(
            TerminalSelectionPhase.Start,
            new Point(20, 20)));
        Assert.True(await surface.SubmitSelectionAsync(
            TerminalSelectionPhase.Update,
            new Point(40, 32)));
        Assert.True(await surface.SubmitSelectionAsync(
            TerminalSelectionPhase.End,
            new Point(40, 32)));

        Assert.Equal(-3, Assert.Single(sink.Scrolls).Lines);
        Assert.Equal(
            [TerminalSelectionPhase.Start, TerminalSelectionPhase.Update, TerminalSelectionPhase.End],
            sink.Selections.Select(selection => selection.Phase));
        Assert.Empty(sink.Mouse);

        surface.UpdateSnapshot(Snapshot(
            mouseTracking: true,
            columns: 60,
            rows: 10,
            revision: 1));
        Assert.False(await surface.ScrollViewportAsync(-3));
        Assert.False(await surface.SubmitMouseAsync(
            TerminalMouseButton.Left,
            TerminalMouseEventKind.Down,
            new Point(20, 20),
            AvaloniaKeyModifiers.Shift));
        Assert.True(await surface.SubmitMouseAsync(
            TerminalMouseButton.WheelUp,
            TerminalMouseEventKind.WheelUp,
            new Point(20, 20),
            AvaloniaKeyModifiers.None));
        Assert.Single(sink.Mouse);
    }

    [Fact]
    public async Task Plain_pointer_click_clears_selection_without_creating_a_selected_cell()
    {
        var sink = new RecordingInputSink();
        var surface = CreateSelectionSurface(sink);
        var click = surface.Metrics.CellBounds(2, 4).Center;

        surface.BeginLocalSelectionGesture(click);
        Assert.False(await surface.UpdateLocalSelectionGestureAsync(click));
        Assert.True(await surface.CompleteLocalSelectionGestureAsync(click));

        var selection = Assert.Single(sink.Selections);
        Assert.Equal(TerminalSelectionPhase.Clear, selection.Phase);
    }

    [Fact]
    public async Task Pointer_drag_starts_updates_and_ends_a_selection()
    {
        var sink = new RecordingInputSink();
        var surface = CreateSelectionSurface(sink);
        var start = surface.Metrics.CellBounds(2, 4).Center;
        var end = surface.Metrics.CellBounds(4, 12).Center;

        surface.BeginLocalSelectionGesture(start);
        Assert.True(await surface.UpdateLocalSelectionGestureAsync(end));
        Assert.True(await surface.CompleteLocalSelectionGestureAsync(end));

        Assert.Equal(
            [TerminalSelectionPhase.Start, TerminalSelectionPhase.Update, TerminalSelectionPhase.End],
            sink.Selections.Select(selection => selection.Phase));
    }

    [Fact]
    public async Task Clipboard_read_and_write_policy_is_enforced_for_user_gestures()
    {
        var sink = new RecordingInputSink
        {
            SelectionText = new TerminalSelectionText("界wrapped\ntext", true, false),
        };
        var clipboard = new RecordingClipboard { Text = "paste me" };
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Clipboard = clipboard,
            Profile = Profile(
                readAccess: TerminalClipboardAccess.Deny,
                writeAccess: TerminalClipboardAccess.Deny),
        };

        await surface.PasteFromClipboardAsync();
        Assert.False(await surface.CopySelectionAsync());
        Assert.Equal(0, clipboard.ReadCount);
        Assert.Empty(clipboard.Writes);
        Assert.Empty(sink.Pastes);

        surface.Profile = Profile(
            readAccess: TerminalClipboardAccess.Allow,
            writeAccess: TerminalClipboardAccess.Allow);
        await surface.PasteFromClipboardAsync();
        Assert.True(await surface.CopySelectionAsync());
        Assert.Equal(1, clipboard.ReadCount);
        Assert.Equal("paste me", Assert.Single(sink.Pastes).Text);
        Assert.Equal("界wrapped\ntext", Assert.Single(clipboard.Writes));
    }

    [Fact]
    public async Task Links_allow_only_http_and_https_and_confirmation_rechecks_live_policy()
    {
        var sink = new RecordingInputSink();
        var opener = new RecordingLinkOpener();
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            LinkOpener = opener,
            Profile = Profile(linkPolicy: TerminalLinkPolicy.ConfirmBeforeOpen),
        };
        surface.Measure(new Size(300, 100));
        surface.Arrange(new Rect(0, 0, 300, 100));
        surface.UpdateSnapshot(LinkSnapshot("https://example.test/first"));
        var point = surface.Metrics.CellBounds(0, 0).Center;

        Assert.True(await surface.ActivateLinkAtAsync(point));
        Assert.True(surface.IsLinkConfirmationVisible);
        surface.UpdateSnapshot(LinkSnapshot("https://example.test/changed", revision: 1));
        Assert.True(await surface.ConfirmPendingLinkAsync());
        Assert.Equal("https://example.test/first", Assert.Single(opener.Opened).AbsoluteUri.TrimEnd('/'));

        surface.UpdateSnapshot(LinkSnapshot("https://example.test/denied", revision: 2));
        Assert.True(await surface.ActivateLinkAtAsync(point));
        surface.Profile = Profile(linkPolicy: TerminalLinkPolicy.Disabled);
        Assert.False(surface.IsLinkConfirmationVisible);
        Assert.False(await surface.ConfirmPendingLinkAsync());
        Assert.Single(opener.Opened);

        surface.Profile = Profile(linkPolicy: TerminalLinkPolicy.Open);
        surface.UpdateSnapshot(LinkSnapshot("javascript:alert(1)", revision: 3));
        Assert.False(await surface.ActivateLinkAtAsync(point));
        surface.UpdateSnapshot(LinkSnapshot("http://example.test/open", revision: 4));
        Assert.True(await surface.ActivateLinkAtAsync(point));
        Assert.Equal(2, opener.Opened.Count);
    }

    [Fact]
    public void Shift_page_shortcuts_map_to_local_page_scrolls_only()
    {
        Assert.True(ManagedTerminalInput.TryMapScrollShortcut(
            Key.PageUp,
            AvaloniaKeyModifiers.Shift,
            23,
            out var pageUp));
        Assert.Equal(-23, pageUp!.Lines);
        Assert.True(ManagedTerminalInput.TryMapScrollShortcut(
            Key.PageDown,
            AvaloniaKeyModifiers.Shift,
            23,
            out var pageDown));
        Assert.Equal(23, pageDown!.Lines);
        Assert.False(ManagedTerminalInput.TryMapScrollShortcut(
            Key.PageUp,
            AvaloniaKeyModifiers.None,
            23,
            out _));
    }

    [Fact]
    public void Presentation_is_always_the_managed_application_renderer()
    {
        var host = new TerminalPresentationHost();

        Assert.IsType<ManagedTerminalSessionHost>(host.Presentation);
    }

    [Fact]
    public async Task Managed_presentation_contains_only_the_application_renderer()
    {
        var host = new TerminalPresentationHost();
        var managed = Assert.IsType<ManagedTerminalSessionHost>(host.Presentation);
        var sink = new RecordingInputSink();
        managed.Surface.InputSink = sink;

        managed.Surface.Profile = Profile(linkPolicy: TerminalLinkPolicy.ConfirmBeforeOpen);
        managed.Surface.SetInputReady(true);
        managed.Surface.UpdateSnapshot(LinkSnapshot("https://example.test/quick"));
        Assert.True(await managed.Surface.ActivateLinkAtAsync(
            managed.Surface.Metrics.CellBounds(0, 0).Center));
        Assert.True(host.TryCancelPendingInteraction());
        Assert.False(managed.Surface.IsLinkConfirmationVisible);
        Assert.False(host.TryCancelPendingInteraction());
    }

    private static TerminalRenderProfileSnapshot Profile(
        double fontSize = 13,
        TerminalClipboardAccess readAccess = TerminalClipboardAccess.Ask,
        TerminalClipboardAccess writeAccess = TerminalClipboardAccess.Allow,
        TerminalLinkPolicy linkPolicy = TerminalLinkPolicy.ConfirmBeforeOpen) => new(
        fontSize,
        TerminalCursorStyle.Block,
        cursorBlink: false,
        10_000,
        TerminalPalette.GhostShellDark,
        lineHeight: 1,
        clipboardPolicy: new TerminalClipboardPolicy(
            readAccess,
            writeAccess,
            TerminalPasteSafetyPolicy.ProtectUnsafeIncludingBracketed),
        linkPolicy: linkPolicy);

    private static ManagedTerminalSurface CreateSelectionSurface(RecordingInputSink sink)
    {
        var surface = new ManagedTerminalSurface
        {
            InputSink = sink,
            Profile = Profile(fontSize: 10),
        };
        surface.Measure(new Size(400, 160));
        surface.Arrange(new Rect(0, 0, 400, 160));
        surface.UpdateSnapshot(Snapshot(mouseTracking: false, columns: 60, rows: 10));
        return surface;
    }

    private static KeymapProfile Keymap(params CommandBinding[] bindings) => new(
        new KeymapProfileId("terminal.custom"),
        "Custom terminal",
        KeymapLayer.Terminal,
        bindings);

    private static CommandBinding Binding(
        CommandId commandId,
        string key,
        CoreKeyModifiers modifiers) => new(
        commandId,
        KeySequence.Of(new KeyStroke(key, modifiers)),
        CommandContext.Terminal);

    private static TerminalScreenSnapshot LinkSnapshot(string hyperlink, long revision = 0) => new(
        "x",
        0,
        0,
        1,
        4,
        false,
        null,
        DateTimeOffset.UtcNow,
        StructuredRows:
        [
            new TerminalScreenRow(
                0,
                [
                    new TerminalScreenCell(
                        "x",
                        1,
                        TerminalCellColor.Default,
                        TerminalCellColor.Default,
                        Hyperlink: hyperlink),
                ]),
        ],
        ContentRevision: revision);

    private static TerminalScreenSnapshot Snapshot(
        bool mouseTracking,
        int columns,
        int rows,
        long revision = 0) => new(
            string.Empty,
            0,
            0,
            rows,
            columns,
            false,
            null,
            DateTimeOffset.UtcNow,
            IsMouseTrackingEnabled: mouseTracking,
            ContentRevision: revision);

    private sealed class RecordingInputSink : IManagedTerminalInputSink
    {
        public List<string> Text { get; } = [];

        public List<TerminalKeyStroke> Keys { get; } = [];

        public List<TerminalMouseInput> Mouse { get; } = [];

        public List<TerminalViewportScrollInput> Scrolls { get; } = [];

        public List<TerminalPhysicalKeyEvent> PhysicalKeys { get; } = [];

        public List<TerminalSelectionInput> Selections { get; } = [];

        public List<TerminalPasteInput> Pastes { get; } = [];

        public List<TerminalFindInput> Finds { get; } = [];

        public bool SupportsFind { get; init; } = true;

        public bool SupportsClearScrollback { get; init; } = true;

        public int ClearScrollbackCount { get; private set; }

        public TerminalSelectionText SelectionText { get; set; } =
            new(string.Empty, false, false);

        public ValueTask SendTextAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Text.Add(text);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendKeyAsync(
            TerminalKeyStroke keyStroke,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Keys.Add(keyStroke);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendPhysicalKeyAsync(
            TerminalPhysicalKeyEvent keyEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PhysicalKeys.Add(keyEvent);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendMouseAsync(
            TerminalMouseInput mouseInput,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mouse.Add(mouseInput);
            return ValueTask.CompletedTask;
        }

        public ValueTask ScrollViewportAsync(
            TerminalViewportScrollInput scrollInput,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scrolls.Add(scrollInput);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> ClearScrollbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearScrollbackCount++;
            return ValueTask.FromResult(SupportsClearScrollback);
        }

        public ValueTask<TerminalFindResult?> FindAsync(
            TerminalFindInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Finds.Add(input);
            if (!SupportsFind)
            {
                return ValueTask.FromResult<TerminalFindResult?>(null);
            }

            if (input.Query.Length == 0)
            {
                return ValueTask.FromResult<TerminalFindResult?>(TerminalFindResult.Empty);
            }

            var selected = (int)(((long)input.RequestedMatchIndex % 3 + 3) % 3);
            return ValueTask.FromResult<TerminalFindResult?>(
                new TerminalFindResult(3, selected, false));
        }

        public ValueTask UpdateSelectionAsync(
            TerminalSelectionInput selectionInput,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Selections.Add(selectionInput);
            return ValueTask.CompletedTask;
        }

        public ValueTask<TerminalSelectionText> ReadSelectionAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SelectionText);
        }

        public ValueTask<TerminalPasteResult> PasteAsync(
            TerminalPasteInput pasteInput,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Pastes.Add(pasteInput);
            return ValueTask.FromResult(TerminalPasteResult.Completed(bracketed: true));
        }
    }

    private sealed class RecordingClipboard : IManagedTerminalClipboard
    {
        public string? Text { get; init; }

        public int ReadCount { get; private set; }

        public List<string> Writes { get; } = [];

        public ValueTask<string?> TryGetTextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(Text);
        }

        public ValueTask SetTextAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(text);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLinkOpener : IManagedTerminalLinkOpener
    {
        public List<Uri> Opened { get; } = [];

        public ValueTask OpenAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(uri);
            return ValueTask.CompletedTask;
        }
    }
}
