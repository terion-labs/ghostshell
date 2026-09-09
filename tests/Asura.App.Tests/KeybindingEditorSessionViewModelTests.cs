using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class KeybindingEditorSessionViewModelTests
{
    [Fact]
    public void Read_only_preset_projects_rows_and_rejects_edits()
    {
        var editor = KeybindingSettingsEditor.Edit(
            BuiltInKeymaps.TmuxApplication,
            expectedRevision: 4,
            BuiltInCommands.Registry);
        using var session = new KeybindingEditorSessionViewModel(editor, isReadOnly: true);
        var copyMode = Row(session, BuiltInCommands.EnterTerminalCopyMode);

        Assert.Equal(BuiltInKeymaps.TmuxApplicationId, session.ProfileId);
        Assert.Equal(KeymapLayer.Application, session.Layer);
        Assert.True(session.IsApplicationLayer);
        Assert.True(session.HasPrefix);
        Assert.False(session.CanEditRows);
        Assert.False(session.CanEditPrefix);
        Assert.False(session.CanSave);
        Assert.All(session.Rows, row => Assert.False(row.CanEdit));
        Assert.False(copyMode.CanUnbind);
        Assert.Equal("Active", copyMode.Status);
        Assert.Contains("read-only", session.StateSummary, StringComparison.OrdinalIgnoreCase);

        var error = Assert.Throws<InvalidOperationException>(() => session.Unbind(copyMode.Id));
        Assert.Contains("clone", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_and_mutations_refresh_the_observable_projection()
    {
        var editor = CloneMacOs();
        using var session = new KeybindingEditorSessionViewModel(editor, isReadOnly: false);
        var copyId = Row(session, BuiltInCommands.Copy).Id;

        Assert.True(session.IsNew);
        Assert.False(session.IsDirty);
        Assert.True(session.CanSave);
        Assert.Contains("not been saved", session.StateSummary, StringComparison.OrdinalIgnoreCase);

        session.Query = "copy";
        var copy = Assert.Single(session.Rows);
        Assert.Equal(copyId, copy.Id);

        session.RecordShortcut(
            copyId,
            [new KeyStroke("C", KeyModifiers.Control | KeyModifiers.Shift)]);

        copy = Assert.Single(session.Rows);
        Assert.Equal("Ctrl+Shift+C", copy.Shortcut);
        Assert.True(copy.CanResetShortcut);
        Assert.True(session.IsDirty);
        Assert.True(session.CanSave);
        Assert.Contains("Unsaved", session.StateSummary, StringComparison.OrdinalIgnoreCase);

        session.Query = "no command can match this";
        Assert.Empty(session.Rows);
        Assert.True(session.HasNoResults);

        session.Query = string.Empty;
        Assert.False(session.HasNoResults);
        Assert.Equal(editor.Rows.Count, session.Rows.Count);
    }

    [Fact]
    public void Blocking_conflict_marks_both_rows_and_prevents_save_until_resolved()
    {
        var editor = CloneMacOs();
        using var session = new KeybindingEditorSessionViewModel(editor, isReadOnly: false);
        var copy = Row(session, BuiltInCommands.Copy);
        var paste = Row(session, BuiltInCommands.Paste);

        session.RecordShortcut(paste.Id, copy.Row.Sequence!.Strokes);

        Assert.Equal(1, session.ConflictCount);
        Assert.True(session.HasConflicts);
        Assert.False(session.CanSave);
        Assert.Equal("Conflict", Row(session, BuiltInCommands.Copy).Status);
        Assert.Equal("Conflict", Row(session, BuiltInCommands.Paste).Status);
        Assert.Contains("Resolve 1", session.StateSummary, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => session.CreateSaveRequest());

        session.Unbind(paste.Id);
        var request = session.CreateSaveRequest();

        Assert.Equal(0, session.ConflictCount);
        Assert.False(session.HasConflicts);
        Assert.True(session.CanSave);
        Assert.DoesNotContain(
            request.Profile.Bindings,
            binding => binding.CommandId == BuiltInCommands.Paste);
    }

    [Fact]
    public void Application_prefix_can_be_recorded_configured_cleared_and_reset()
    {
        var editor = KeybindingSettingsEditor.ClonePreset(
            BuiltInKeymaps.TmuxApplication,
            new KeymapProfileId("custom.tmux.session"),
            "Custom tmux",
            BuiltInCommands.Registry);
        using var session = new KeybindingEditorSessionViewModel(editor, isReadOnly: false);
        var replacement = new KeyStroke("A", KeyModifiers.Control);

        session.RecordPrefix(replacement);
        session.UpdatePrefixOptions(
            timeoutMilliseconds: 1_500,
            repeatable: false,
            FailedSequenceBehavior.PassThrough);

        Assert.Equal("Ctrl+A", session.PrefixShortcut);
        Assert.Equal(1_500, session.PrefixTimeoutMilliseconds);
        Assert.False(session.PrefixRepeatable);
        Assert.Equal(FailedSequenceBehavior.PassThrough, session.PrefixFailedBehavior);

        session.ClearPrefix();
        Assert.False(session.HasPrefix);
        Assert.Equal("No prefix", session.PrefixShortcut);
        Assert.Throws<InvalidOperationException>(() => session.UpdatePrefixOptions(
            timeoutMilliseconds: 500,
            repeatable: true,
            FailedSequenceBehavior.DiscardAndShowHint));

        session.ResetAll();
        Assert.Equal(BuiltInKeymaps.TmuxApplication.Prefix, editor.Prefix);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Terminal_profile_cannot_define_an_application_prefix()
    {
        var editor = CloneMacOs();
        using var session = new KeybindingEditorSessionViewModel(editor, isReadOnly: false);

        Assert.False(session.IsApplicationLayer);
        Assert.False(session.CanEditPrefix);
        var error = Assert.Throws<InvalidOperationException>(() => session.RecordPrefix(
            new KeyStroke("B", KeyModifiers.Control)));
        Assert.Contains("application keymaps", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dispose_detaches_the_projection_from_the_editor()
    {
        var editor = CloneMacOs();
        var session = new KeybindingEditorSessionViewModel(editor, isReadOnly: false);
        var copy = Row(session, BuiltInCommands.Copy);

        session.Dispose();
        editor.Unbind(copy.Id);

        Assert.False(Assert.Single(editor.Rows, row => row.Id == copy.Id).IsBound);
        Assert.True(Assert.Single(session.Rows, row => row.Id == copy.Id).IsBound);
        Assert.Throws<ObjectDisposedException>(() => session.Unbind(copy.Id));
    }

    private static KeybindingSettingsEditor CloneMacOs() =>
        KeybindingSettingsEditor.ClonePreset(
            BuiltInKeymaps.MacOsTerminal,
            new KeymapProfileId("custom.macos.session"),
            "Custom macOS",
            BuiltInCommands.Registry);

    private static KeybindingEditorRowItemViewModel Row(
        KeybindingEditorSessionViewModel session,
        CommandId commandId) =>
        Assert.Single(session.Rows, row => row.Row.CommandId == commandId);
}
