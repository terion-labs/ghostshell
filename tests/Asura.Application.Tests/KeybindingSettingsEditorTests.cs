using Asura.Core;

namespace Asura.Application.Tests;

public sealed class KeybindingSettingsEditorTests
{
    [Fact]
    public void Cloned_preset_records_shortcuts_and_emits_a_create_request()
    {
        var editor = KeybindingSettingsEditor.ClonePreset(
            BuiltInKeymaps.MacOsTerminal,
            new KeymapProfileId("custom.macos"),
            "My macOS shortcuts",
            BuiltInCommands.Registry);
        var copy = Row(editor, BuiltInCommands.Copy);
        var changeCount = 0;
        editor.Changed += (_, _) => changeCount++;

        editor.RecordShortcut(
            copy.Id,
            [new KeyStroke("c", KeyModifiers.Control | KeyModifiers.Shift)]);
        var request = editor.CreateSaveRequest();

        Assert.Null(request.ExpectedRevision);
        Assert.Equal(new KeymapProfileId("custom.macos"), request.Profile.Id);
        Assert.Equal(BuiltInKeymaps.MacOsTerminalId, request.Profile.BasedOn);
        Assert.Equal("My macOS shortcuts", request.Profile.Name);
        Assert.Equal(
            KeySequence.Of(new KeyStroke("C", KeyModifiers.Control | KeyModifiers.Shift)),
            Binding(request.Profile, BuiltInCommands.Copy).Sequence);
        Assert.True(editor.IsDirty);
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void Search_matches_command_metadata_arguments_and_binding_state()
    {
        var editor = CloneTmux();
        var topBottom = Assert.Single(editor.Search("top-bottom"));

        editor.Unbind(topBottom.Id);

        Assert.Equal(BuiltInCommands.SplitPanel, topBottom.CommandId);
        Assert.All(editor.Search("Panels"), row => Assert.Equal("Panels", row.Category));
        Assert.Contains(editor.Search("panel.split"), row => row.Id == topBottom.Id);
        Assert.Contains(editor.Search("unbound"), row => row.Id == topBottom.Id);
    }

    [Fact]
    public void Unbind_and_reset_keep_a_stable_recorder_target()
    {
        var editor = KeybindingSettingsEditor.ClonePreset(
            BuiltInKeymaps.LinuxTerminal,
            new KeymapProfileId("custom.linux"),
            "Linux custom",
            BuiltInCommands.Registry);
        var paste = Row(editor, BuiltInCommands.Paste);

        editor.RecordShortcut(
            paste.Id,
            [new KeyStroke("v", KeyModifiers.Control | KeyModifiers.Alt)]);
        Assert.Equal("Ctrl+Alt+V", Row(editor, paste.Id).Shortcut);

        editor.Unbind(paste.Id);
        Assert.False(Row(editor, paste.Id).IsBound);
        Assert.Contains(Row(editor, paste.Id), editor.Search("unbound"));

        editor.ResetShortcut(paste.Id);
        Assert.Equal("Ctrl+Shift+V", Row(editor, paste.Id).Shortcut);
        Assert.False(Row(editor, paste.Id).CanReset);
    }

    [Fact]
    public void Application_prefix_can_be_edited_cleared_and_reset()
    {
        var editor = CloneTmux();
        var replacement = new PrefixConfiguration(
            new KeyStroke("A", KeyModifiers.Control),
            TimeSpan.FromSeconds(2),
            repeatable: false,
            FailedSequenceBehavior.PassThrough);

        editor.SetPrefix(replacement);
        Assert.Equal(replacement, editor.Prefix);

        editor.SetPrefix(null);
        Assert.Null(editor.Prefix);

        editor.ResetPrefix();
        Assert.Equal(BuiltInKeymaps.TmuxApplication.Prefix, editor.Prefix);
    }

    [Fact]
    public void Blocking_conflicts_are_attached_to_both_rows_and_prevent_save()
    {
        var editor = KeybindingSettingsEditor.ClonePreset(
            BuiltInKeymaps.MacOsTerminal,
            new KeymapProfileId("custom.conflict"),
            "Conflicting",
            BuiltInCommands.Registry);
        var copy = Row(editor, BuiltInCommands.Copy);
        var paste = Row(editor, BuiltInCommands.Paste);

        editor.SetShortcut(paste.Id, copy.Sequence!);

        var issue = Assert.Single(
            editor.Issues,
            item => item.Severity == KeymapIssueSeverity.Error);
        Assert.Equal(KeymapIssueKind.ExactBinding, issue.Kind);
        Assert.True(Row(editor, copy.Id).HasBlockingConflict);
        Assert.True(Row(editor, paste.Id).HasBlockingConflict);
        Assert.Contains(editor.Search("conflict"), row => row.Id == paste.Id);
        Assert.False(editor.CanSave);
        Assert.Throws<InvalidOperationException>(() => editor.CreateSaveRequest());

        editor.Unbind(paste.Id);
        Assert.True(editor.CanSave);
        _ = editor.CreateSaveRequest();
    }

    [Fact]
    public void Reset_to_preset_preserves_unknown_commands_during_a_downgrade()
    {
        var unknownId = new CommandId("future.terminal.magic");
        var profile = new KeymapProfile(
            new KeymapProfileId("custom.with-future-command"),
            "Future-aware profile",
            KeymapLayer.Terminal,
            [
                new CommandBinding(
                    BuiltInCommands.Copy,
                    KeySequence.Of(new KeyStroke("C", KeyModifiers.Control | KeyModifiers.Alt)),
                    CommandContext.Terminal),
                new CommandBinding(
                    unknownId,
                    KeySequence.Of(new KeyStroke("U", KeyModifiers.Control | KeyModifiers.Alt)),
                    CommandContext.Terminal),
            ],
            basedOn: BuiltInKeymaps.MacOsTerminalId);
        var editor = KeybindingSettingsEditor.Edit(
            profile,
            expectedRevision: 42,
            BuiltInCommands.Registry,
            BuiltInKeymaps.MacOsTerminal);

        editor.ResetBindingsAndPrefix();
        editor.Rename("Reset, future preserved");
        var request = editor.CreateSaveRequest();

        Assert.Equal(42, request.ExpectedRevision);
        Assert.Equal(
            Binding(BuiltInKeymaps.MacOsTerminal, BuiltInCommands.Copy).Sequence,
            Binding(request.Profile, BuiltInCommands.Copy).Sequence);
        Assert.Equal(
            KeySequence.Of(new KeyStroke("U", KeyModifiers.Control | KeyModifiers.Alt)),
            Binding(request.Profile, unknownId).Sequence);
        Assert.Contains(editor.Issues, issue => issue.Kind == KeymapIssueKind.UnknownCommand);
        Assert.True(editor.CanSave);
        Assert.Single(editor.Search("unknown"));
    }

    [Fact]
    public void Imported_profile_round_trips_as_the_durable_export_shape()
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orientation"] = "left-right",
        };
        var imported = new KeymapProfile(
            new KeymapProfileId("imported.application"),
            "Imported",
            KeymapLayer.Application,
            [
                new CommandBinding(
                    BuiltInCommands.SplitPanel,
                    KeySequence.Of(
                        new KeyStroke("B", KeyModifiers.Control),
                        new KeyStroke("%")),
                    CommandContext.Panel,
                    arguments),
            ],
            BuiltInKeymaps.TmuxApplication.Prefix,
            BuiltInKeymaps.TmuxApplicationId);

        var editor = KeybindingSettingsEditor.Import(imported, BuiltInCommands.Registry);
        var exported = editor.CreateDraftProfile();
        var request = editor.CreateSaveRequest();

        Assert.False(editor.IsDirty);
        Assert.Null(request.ExpectedRevision);
        Assert.Equal(imported.Id, exported.Id);
        Assert.Equal(imported.BasedOn, exported.BasedOn);
        Assert.Equal(imported.Prefix, exported.Prefix);
        Assert.Equal("left-right", Assert.Single(exported.Bindings).Arguments["orientation"]);
    }

    [Fact]
    public void Imported_multi_stroke_terminal_binding_is_preserved_but_blocks_save_until_repaired()
    {
        var imported = new KeymapProfile(
            new KeymapProfileId("imported.terminal.sequence"),
            "Imported terminal sequence",
            KeymapLayer.Terminal,
            [
                new CommandBinding(
                    BuiltInCommands.Copy,
                    KeySequence.Of(
                        new KeyStroke("B", KeyModifiers.Control),
                        new KeyStroke("C")),
                    CommandContext.Terminal),
            ]);
        var editor = KeybindingSettingsEditor.Import(imported, BuiltInCommands.Registry);
        var row = Assert.Single(editor.Rows);

        Assert.Equal(imported.Bindings[0].Sequence, Assert.Single(editor.CreateDraftProfile().Bindings).Sequence);
        Assert.Contains(editor.Issues, issue => issue.Kind == KeymapIssueKind.TerminalSequence);
        Assert.False(editor.CanSave);

        editor.SetShortcut(row.Id, KeySequence.Of(new KeyStroke("C", KeyModifiers.Meta)));

        Assert.True(editor.CanSave);
        Assert.Equal(1, Assert.Single(editor.CreateSaveRequest().Profile.Bindings).Sequence.Count);
    }

    [Fact]
    public void Added_binding_participates_in_validation_and_can_be_removed()
    {
        var editor = KeybindingSettingsEditor.Import(
            new KeymapProfile(
                new KeymapProfileId("empty.application"),
                "Empty",
                KeymapLayer.Application,
                []),
            BuiltInCommands.Registry);
        var sequence = KeySequence.Of(new KeyStroke("K", KeyModifiers.Control));
        _ = editor.AddBinding(new CommandBinding(
            BuiltInCommands.NewTab,
            sequence,
            CommandContext.Workspace));
        var conflicting = editor.AddBinding(new CommandBinding(
            BuiltInCommands.CloseTab,
            sequence,
            CommandContext.Workspace));

        Assert.False(editor.CanSave);

        editor.Unbind(conflicting);
        var request = editor.CreateSaveRequest();

        Assert.Single(request.Profile.Bindings);
        Assert.Equal(BuiltInCommands.NewTab, request.Profile.Bindings[0].CommandId);
    }

    [Fact]
    public void Reset_source_must_match_the_edited_layer()
    {
        var error = Assert.Throws<ArgumentException>(() => KeybindingSettingsEditor.Edit(
            BuiltInKeymaps.MacOsTerminal,
            expectedRevision: 1,
            BuiltInCommands.Registry,
            BuiltInKeymaps.TmuxApplication));

        Assert.Contains("same layer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static KeybindingSettingsEditor CloneTmux() =>
        KeybindingSettingsEditor.ClonePreset(
            BuiltInKeymaps.TmuxApplication,
            new KeymapProfileId("custom.tmux"),
            "Custom tmux",
            BuiltInCommands.Registry);

    private static KeybindingEditorRow Row(
        KeybindingSettingsEditor editor,
        CommandId commandId) =>
        Assert.Single(editor.Rows, row => row.CommandId == commandId);

    private static KeybindingEditorRow Row(
        KeybindingSettingsEditor editor,
        KeybindingEditorRowId rowId) =>
        Assert.Single(editor.Rows, row => row.Id == rowId);

    private static CommandBinding Binding(KeymapProfile profile, CommandId commandId) =>
        Assert.Single(profile.Bindings, binding => binding.CommandId == commandId);
}
