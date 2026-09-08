using GhostShell.App.ViewModels;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class TerminalProfileEditorViewModelTests
{
    [Fact]
    public void Bundled_terminal_family_is_always_available_in_the_editor()
    {
        var editor = new TerminalProfileEditorViewModel(
            DefaultProfile("Unavailable custom family"),
            expectedRevision: 1);

        Assert.Contains("JetBrains Mono", editor.FontFamilies, StringComparer.Ordinal);
        Assert.Contains("Unavailable custom family", editor.FontFamilies, StringComparer.Ordinal);
    }

    [Fact]
    public void SaveRequestBuildsCompleteRendererProfileAndPreservesIdentity()
    {
        var original = DefaultProfile();
        var editor = new TerminalProfileEditorViewModel(original, expectedRevision: 12)
        {
            FontFamily = "Berkeley Mono",
            FontSize = 15.5,
            LineHeight = 1.25,
            ScrollbackLines = 250_000,
            CursorStyle = TerminalCursorStyle.Bar,
            CursorBlink = false,
            Foreground = "#F0E8DD",
            Background = "#101113",
            Cursor = "#FF8400",
            Selection = "#5A3B24",
            ClipboardRead = TerminalClipboardAccess.Deny,
            ClipboardWrite = TerminalClipboardAccess.Ask,
            LinkPolicy = TerminalLinkPolicy.Disabled,
            ImeEnabled = false,
            ShellIntegration = TerminalShellIntegrationMode.Zsh,
            BellMode = TerminalBellMode.Disabled,
            Compatibility = TerminalCompatibilityProfile.Xterm256Color,
        };

        var request = editor.CreateSaveRequest();

        Assert.Equal(12, request.ExpectedRevision);
        Assert.Equal(original.Id, request.Profile.Id);
        Assert.Equal(original.Name, request.Profile.Name);
        Assert.Equal(original.KeymapId, request.Profile.KeymapId);
        Assert.Equal("Berkeley Mono", request.Profile.FontFamily);
        Assert.Equal(15.5, request.Profile.FontSize);
        Assert.Equal(1.25, request.Profile.LineHeight);
        Assert.Equal(250_000, request.Profile.ScrollbackLines);
        Assert.Equal(TerminalCursorStyle.Bar, request.Profile.CursorStyle);
        Assert.False(request.Profile.CursorBlink);
        Assert.Equal(RgbColor.Parse("#F0E8DD"), request.Profile.Palette.Foreground);
        Assert.Equal(RgbColor.Parse("#101113"), request.Profile.Palette.Background);
        Assert.Equal(RgbColor.Parse("#FF8400"), request.Profile.Palette.Cursor);
        Assert.Equal(RgbColor.Parse("#5A3B24"), request.Profile.Palette.SelectionBackground);
        Assert.Equal(original.Palette.AnsiColors, request.Profile.Palette.AnsiColors);
        Assert.Equal(TerminalClipboardAccess.Deny, request.Profile.ClipboardPolicy.ReadAccess);
        Assert.Equal(TerminalClipboardAccess.Ask, request.Profile.ClipboardPolicy.WriteAccess);
        Assert.Equal(
            original.ClipboardPolicy.PasteSafety,
            request.Profile.ClipboardPolicy.PasteSafety);
        Assert.Equal(TerminalLinkPolicy.Disabled, request.Profile.LinkPolicy);
        Assert.False(request.Profile.ImeEnabled);
        Assert.Equal(TerminalShellIntegrationMode.Zsh, request.Profile.ShellIntegration);
        Assert.Equal(TerminalBellMode.Disabled, request.Profile.BellMode);
        Assert.Equal(TerminalCompatibilityProfile.Xterm256Color, request.Profile.Compatibility);
    }

    [Fact]
    public void InvalidPaletteValueFailsBeforePersistence()
    {
        var editor = new TerminalProfileEditorViewModel(DefaultProfile(), expectedRevision: 1)
        {
            Background = "not-a-color",
        };

        var error = Assert.Throws<FormatException>(() => editor.CreateSaveRequest());

        Assert.Contains("six hexadecimal digits", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Low_contrast_custom_colors_are_reported_before_save()
    {
        var editor = new TerminalProfileEditorViewModel(DefaultProfile(), expectedRevision: 1)
        {
            Background = "#111111",
            Foreground = "#111111",
            Cursor = "#111111",
        };

        Assert.True(editor.HasContrastWarning);
        Assert.Contains("text is 1.00:1", editor.ContrastWarning, StringComparison.Ordinal);
        Assert.Contains("cursor is 1.00:1", editor.ContrastWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileRangeValidationIsAppliedToEditedValues()
    {
        var editor = new TerminalProfileEditorViewModel(DefaultProfile(), expectedRevision: 1)
        {
            ScrollbackLines = 10_000_001,
        };

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => editor.CreateSaveRequest());
    }

    [Fact]
    public void TerminalKeymapOptionsExcludeApplicationProfilesAndPersistTheSelection()
    {
        var selected = TerminalKeymap("keymap-test", "Selected");
        var replacement = TerminalKeymap("keymap-replacement", "Replacement");
        var application = new KeymapProfile(
            new KeymapProfileId("application-map"),
            "Application",
            KeymapLayer.Application,
            [],
            BuiltInKeymaps.TmuxApplication.Prefix);
        var editor = new TerminalProfileEditorViewModel(
            DefaultProfile(),
            expectedRevision: 3,
            [replacement, application, selected]);

        Assert.Equal(["Replacement", "Selected"], editor.TerminalKeymaps.Select(option => option.Name), StringComparer.Ordinal);
        Assert.Equal(selected.Id, editor.SelectedKeymap.Id);
        Assert.True(editor.MatchesTerminalKeymaps([selected, replacement, application]));
        Assert.False(editor.MatchesTerminalKeymaps([selected]));

        editor.SelectedKeymap = editor.TerminalKeymaps.Single(option => option.Id == replacement.Id);

        Assert.Equal(replacement.Id, editor.CreateSaveRequest().Profile.KeymapId);
    }

    [Fact]
    public void MissingSelectedKeymapRemainsVisibleAndRoundTrips()
    {
        var editor = new TerminalProfileEditorViewModel(
            DefaultProfile(),
            expectedRevision: 8,
            [TerminalKeymap("different-map", "Different")]);

        Assert.False(editor.SelectedKeymap.IsAvailable);
        Assert.Contains("missing", editor.SelectedKeymap.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DefaultProfile().KeymapId, editor.CreateSaveRequest().Profile.KeymapId);
    }

    private static TerminalProfile DefaultProfile(string fontFamily = "JetBrains Mono") => new(
        new TerminalProfileId("terminal-editor-test"),
        "Terminal editor test",
        fontFamily,
        14,
        1.4,
        TerminalCursorStyle.Block,
        cursorBlink: true,
        100_000,
        TerminalPalette.GhostShellDark,
        new KeymapProfileId("keymap-test"));

    private static KeymapProfile TerminalKeymap(string id, string name) => new(
        new KeymapProfileId(id),
        name,
        KeymapLayer.Terminal,
        []);
}
