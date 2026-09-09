using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class TerminalPalettePresetTests
{
    private static TerminalProfile Profile(TerminalPalette palette) => new(
        new TerminalProfileId("builtin.terminal.default"),
        "Default terminal",
        "JetBrains Mono",
        14,
        1.4,
        TerminalCursorStyle.Block,
        cursorBlink: true,
        100_000,
        palette,
        BuiltInKeymaps.MacOsTerminalId);

    private static TerminalProfileEditorViewModel Editor(TerminalPalette? palette = null) =>
        new(Profile(palette ?? TerminalPalette.AsuraDark), 1, BuiltInKeymaps.All);

    [Fact]
    public void Every_preset_is_offered()
    {
        var editor = Editor();
        Assert.Equal(
            TerminalPalette.Presets.Select(preset => preset.Name),
            editor.PalettePresets.Select(option => option.Name), StringComparer.Ordinal);
    }

    [Fact]
    public void The_profiles_own_palette_starts_selected()
    {
        var editor = Editor(TerminalPalette.Nord);

        Assert.Equal("Nord", editor.PaletteName);
        Assert.Equal("Nord", editor.SelectedPalettePreset?.Name);
        Assert.Single(editor.PalettePresets, option => option.IsSelected);
    }

    [Fact]
    public void Applying_a_preset_replaces_the_visible_colours()
    {
        var editor = Editor();

        editor.ApplyPalettePreset(TerminalPalette.Dracula);

        Assert.Equal(TerminalPalette.Dracula.Background.ToString(), editor.Background);
        Assert.Equal(TerminalPalette.Dracula.Foreground.ToString(), editor.Foreground);
        Assert.Equal(TerminalPalette.Dracula.Cursor.ToString(), editor.Cursor);
        Assert.Equal(
            TerminalPalette.Dracula.SelectionBackground.ToString(),
            editor.Selection);
    }

    [Fact]
    public void Applying_a_preset_also_replaces_the_ansi_colours()
    {
        var editor = Editor();

        editor.ApplyPalettePreset(TerminalPalette.Solarized);

        Assert.Equal(
            TerminalPalette.Solarized.AnsiColors.Take(8).Select(color => color.ToString()),
            editor.NormalAnsiColors.Select(swatch => swatch.Hex), StringComparer.Ordinal);
    }

    [Fact]
    public void Applying_a_preset_moves_the_selection()
    {
        var editor = Editor();

        editor.ApplyPalettePreset(TerminalPalette.Midnight);

        Assert.Equal("Midnight", editor.SelectedPalettePreset?.Name);
        Assert.Single(editor.PalettePresets, option => option.IsSelected);
        Assert.True(
            editor.PalettePresets.Single(option => string.Equals(option.Name, "Midnight", StringComparison.Ordinal)).IsSelected);
    }

    [Fact]
    public void A_saved_profile_carries_the_whole_preset()
    {
        var editor = Editor();
        editor.ApplyPalettePreset(TerminalPalette.Nord);

        var saved = editor.CreateSaveRequest().Profile.Palette;

        Assert.Equal("Nord", saved.Name);
        Assert.True(saved.Matches(TerminalPalette.Nord));
    }

    [Fact]
    public void Editing_a_colour_leaves_every_preset_unselected()
    {
        var editor = Editor();
        Assert.NotNull(editor.SelectedPalettePreset);

        editor.Background = "#101010";

        Assert.Null(editor.SelectedPalettePreset);
        Assert.Equal("Custom", editor.PaletteName);
        Assert.DoesNotContain(editor.PalettePresets, option => option.IsSelected);
    }

    [Fact]
    public void Editing_a_colour_back_onto_a_preset_reselects_it()
    {
        var editor = Editor();
        editor.Background = "#101010";
        Assert.Null(editor.SelectedPalettePreset);

        editor.Background = TerminalPalette.AsuraDark.Background.ToString();

        Assert.Equal("Asura Dark", editor.SelectedPalettePreset?.Name);
    }

    [Fact]
    public void A_half_typed_colour_does_not_throw()
    {
        var editor = Editor();

        editor.Background = "#12";

        Assert.Null(editor.SelectedPalettePreset);
        Assert.Equal("Custom", editor.PaletteName);
    }

    [Fact]
    public void Presets_are_matched_by_colour_rather_than_by_name()
    {
        var renamed = new TerminalPalette(
            "Something else",
            TerminalPalette.Nord.Foreground,
            TerminalPalette.Nord.Background,
            TerminalPalette.Nord.Cursor,
            TerminalPalette.Nord.SelectionBackground,
            TerminalPalette.Nord.AnsiColors);

        Assert.True(renamed.Matches(TerminalPalette.Nord));
        Assert.False(TerminalPalette.Nord.Matches(TerminalPalette.Dracula));
    }

    [Fact]
    public void Every_preset_defines_a_full_sixteen_colour_palette() =>
        Assert.All(
            TerminalPalette.Presets,
            preset => Assert.Equal(16, preset.AnsiColors.Count));

    [Fact]
    public void Preset_names_are_distinct() =>
        Assert.Equal(
            TerminalPalette.Presets.Count,
            TerminalPalette.Presets.Select(preset => preset.Name).Distinct(StringComparer.Ordinal).Count());
}

public sealed class TerminalAppearanceEditorTests
{
    private static TerminalProfile Profile(string fontFamily = "JetBrains Mono") => new(
        new TerminalProfileId("builtin.terminal.default"),
        "Default terminal",
        fontFamily,
        14,
        1.4,
        TerminalCursorStyle.Block,
        cursorBlink: true,
        100_000,
        TerminalPalette.AsuraDark,
        BuiltInKeymaps.MacOsTerminalId);

    private static TerminalProfileEditorViewModel Editor(string fontFamily = "JetBrains Mono") =>
        new(Profile(fontFamily), 1, BuiltInKeymaps.All);

    [Fact]
    public void The_profiles_font_is_offered_even_when_it_is_not_installed()
    {
        var editor = Editor("A Font Nobody Has Installed");

        Assert.Contains("A Font Nobody Has Installed", editor.FontFamilies, StringComparer.Ordinal);
    }

    [Fact]
    public void Font_families_are_distinct_and_sorted()
    {
        var families = Editor().FontFamilies;

        Assert.Equal(families.Distinct(StringComparer.OrdinalIgnoreCase).Count(), families.Count);
        Assert.Equal(
            families.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase),
            families, StringComparer.Ordinal);
    }

    [Fact]
    public void Picking_a_colour_writes_the_six_digit_hex_the_palette_stores()
    {
        var editor = Editor();

        editor.BackgroundColor = Avalonia.Media.Color.FromArgb(0x80, 0x12, 0x34, 0x56);

        Assert.Equal("#123456", editor.Background);
    }

    [Fact]
    public void Typing_a_hex_value_moves_the_picker()
    {
        var editor = Editor();

        editor.Foreground = "#0A0B0C";

        Assert.Equal(Avalonia.Media.Color.FromRgb(0x0A, 0x0B, 0x0C), editor.ForegroundColor);
    }

    [Fact]
    public void A_half_typed_hex_leaves_the_picker_transparent_rather_than_throwing()
    {
        var editor = Editor();

        editor.Cursor = "#12";

        Assert.Equal(Avalonia.Media.Colors.Transparent, editor.CursorColor);
    }

    [Fact]
    public void Applying_a_preset_moves_every_picker()
    {
        var editor = Editor();

        editor.ApplyPalettePreset(TerminalPalette.Nord);

        Assert.Equal(
            Avalonia.Media.Color.Parse(TerminalPalette.Nord.Background.ToString()),
            editor.BackgroundColor);
        Assert.Equal(
            Avalonia.Media.Color.Parse(TerminalPalette.Nord.Foreground.ToString()),
            editor.ForegroundColor);
    }
}
