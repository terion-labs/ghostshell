using Asura.Application;

namespace Asura.Application.Tests;

public sealed class TerminalContractsTests
{
    [Fact]
    public void Screen_snapshot_defensively_copies_structured_rows()
    {
        var cells = new List<TerminalScreenCell>
        {
            new("界", 2, TerminalCellColor.Default, TerminalCellColor.Default),
        };
        var rows = new List<TerminalScreenRow>
        {
            new(0, cells),
        };
        var snapshot = new TerminalScreenSnapshot(
            "界",
            0,
            0,
            1,
            2,
            false,
            null,
            DateTimeOffset.UnixEpoch,
            StructuredRows: rows,
            ContentRevision: 4);

        cells.Clear();
        rows.Clear();

        Assert.Single(snapshot.StructuredRows);
        Assert.Single(snapshot.StructuredRows[0].Cells);
        Assert.Equal("界", snapshot.StructuredRows[0].Cells[0].Text);
        Assert.Equal(4, snapshot.ContentRevision);
    }

    [Fact]
    public void Interactive_state_is_expiring_observation_not_authority()
    {
        var observedAt = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var state = new TerminalInteractiveStateSnapshot(
            7,
            TerminalInteractiveStateKind.ApprovalRequired,
            observedAt,
            observedAt.AddSeconds(5));
        var snapshot = new TerminalScreenSnapshot(
            "Approve?",
            0,
            0,
            1,
            80,
            false,
            null,
            observedAt,
            InteractiveState: state);

        Assert.Same(state, snapshot.InteractiveState);
        Assert.Equal(TerminalInteractiveStateKind.ApprovalRequired, state.Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalInteractiveStateSnapshot(
                8,
                TerminalInteractiveStateKind.Working,
                observedAt,
                observedAt));
    }

    [Fact]
    public void Screen_snapshot_rejects_cells_outside_the_declared_viewport()
    {
        var row = new TerminalScreenRow(
            0,
            [
                new TerminalScreenCell("界", 2, TerminalCellColor.Default, TerminalCellColor.Default),
            ]);

        Assert.Throws<ArgumentException>(() => new TerminalScreenSnapshot(
            "界",
            0,
            0,
            1,
            1,
            false,
            null,
            DateTimeOffset.UnixEpoch,
            StructuredRows: [row]));
    }

    [Fact]
    public void Input_contracts_reject_unknown_values_and_unbounded_paste()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalKeyStroke((TerminalKey)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalMouseInput(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                -1,
                0));
        Assert.Throws<ArgumentException>(() =>
            new TerminalPasteInput(new string('x', TerminalPasteInput.MaximumCharacters + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalViewportScrollInput(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalSelectionInput(TerminalSelectionPhase.Start, -1, 0));
        Assert.Throws<ArgumentException>(() =>
            new TerminalSelectionText(
                new string('x', TerminalSelectionText.MaximumCharacters + 1),
                true,
                false));
        Assert.Throws<ArgumentException>(() =>
            new TerminalFindInput(new string('x', TerminalFindInput.MaximumQueryLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalFindResult(0, 0, false));
        Assert.Throws<ArgumentException>(() =>
            new TerminalPhysicalKeyEvent(
                TerminalPhysicalKey.A,
                "A",
                "A",
                TerminalKeyModifiers.Shift,
                TerminalKeyModifiers.Control,
                TerminalKeyAction.Press));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalPhysicalKeyEvent(
                TerminalPhysicalKey.A,
                "A",
                "A",
                TerminalKeyModifiers.None,
                TerminalKeyModifiers.None,
                TerminalKeyAction.Press,
                0xD800));
    }

    [Fact]
    public void Physical_key_contract_preserves_protocol_relevant_platform_fields()
    {
        var keyEvent = new TerminalPhysicalKeyEvent(
            TerminalPhysicalKey.A,
            "A",
            "A",
            TerminalKeyModifiers.Shift | TerminalKeyModifiers.RightShift,
            TerminalKeyModifiers.Shift | TerminalKeyModifiers.RightShift,
            TerminalKeyAction.Repeat,
            'a',
            IsComposing: true);

        Assert.Equal(TerminalPhysicalKey.A, keyEvent.PhysicalKey);
        Assert.Equal("A", keyEvent.LogicalKey);
        Assert.Equal("A", keyEvent.Text);
        Assert.Equal(TerminalKeyAction.Repeat, keyEvent.Action);
        Assert.Equal((uint)'a', keyEvent.UnshiftedCodepoint);
        Assert.True(keyEvent.IsComposing);
    }

    [Theory]
    [InlineData('d', TerminalCharacterChordModifier.Control)]
    [InlineData('x', TerminalCharacterChordModifier.Alt)]
    public void Character_chord_accepts_only_canonical_lowercase_ascii_letters(
        char character,
        TerminalCharacterChordModifier modifier)
    {
        var chord = new TerminalCharacterChord(character, modifier);

        Assert.Equal(character, chord.Character);
        Assert.Equal(modifier, chord.Modifier);
    }

    [Theory]
    [InlineData('D')]
    [InlineData('0')]
    [InlineData(' ')]
    [InlineData('\u00E9')]
    public void Character_chord_rejects_noncanonical_characters(char character)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalCharacterChord(
                character,
                TerminalCharacterChordModifier.Control));
    }

    [Fact]
    public void Character_chord_rejects_unknown_modifiers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalCharacterChord(
                'd',
                (TerminalCharacterChordModifier)999));
    }

    [Theory]
    [InlineData(TerminalMouseButton.None, TerminalMouseEventKind.Down)]
    [InlineData(TerminalMouseButton.Left, TerminalMouseEventKind.Move)]
    [InlineData(TerminalMouseButton.Right, TerminalMouseEventKind.WheelUp)]
    [InlineData(TerminalMouseButton.WheelUp, TerminalMouseEventKind.WheelDown)]
    [InlineData(TerminalMouseButton.WheelDown, TerminalMouseEventKind.Drag)]
    public void Mouse_input_rejects_mismatched_button_and_event_kind(
        TerminalMouseButton button,
        TerminalMouseEventKind kind)
    {
        Assert.Throws<ArgumentException>(() =>
            new TerminalMouseInput(button, kind, 0, 0));
    }

    [Fact]
    public void Screen_snapshot_validates_and_copies_command_boundaries()
    {
        var boundaries = new List<TerminalCommandBoundary>
        {
            new(1, TerminalCommandBoundaryKind.PromptStarted, 0, 0),
            new(2, TerminalCommandBoundaryKind.CommandFinished, 1, 3, 7),
        };
        var snapshot = new TerminalScreenSnapshot(
            string.Empty,
            0,
            0,
            2,
            8,
            false,
            null,
            DateTimeOffset.UnixEpoch,
            WindowTitle: "remote",
            IsCursorVisible: false,
            ScrollbackLinesAbove: 10,
            ScrollbackLinesBelow: 2,
            CommandBoundaries: boundaries);

        boundaries.Clear();

        Assert.Equal("remote", snapshot.WindowTitle);
        Assert.False(snapshot.IsCursorVisible);
        Assert.False(snapshot.IsViewportAtBottom);
        Assert.Equal(2, snapshot.CommandBoundaries.Count);
        Assert.Equal(7, snapshot.CommandBoundaries[1].ExitCode);
        Assert.Throws<ArgumentException>(() => new TerminalScreenSnapshot(
            string.Empty,
            0,
            0,
            1,
            1,
            false,
            null,
            DateTimeOffset.UnixEpoch,
            CommandBoundaries:
            [
                new TerminalCommandBoundary(
                    1,
                    TerminalCommandBoundaryKind.PromptStarted,
                    1,
                    0),
            ]));
    }

    [Theory]
    [InlineData("single line")]
    [InlineData("first\nsecond")]
    [InlineData("control\u0003")]
    [InlineData("tab\tallowed")]
    public void Paste_contract_preserves_text_for_engine_encoding(string text)
    {
        Assert.Equal(text, new TerminalPasteInput(text).Text);
    }
}
