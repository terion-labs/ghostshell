using Asura.App.Views.Components;
using Avalonia.Input;

namespace Asura.App.Tests;

public sealed class DatabaseDiagramKeyboardShortcutTests
{
    [Theory]
    [InlineData(Key.OemPlus, KeyModifiers.Meta, null)]
    [InlineData(Key.OemPlus, KeyModifiers.Meta | KeyModifiers.Shift, "+")]
    [InlineData(Key.Add, KeyModifiers.Control, "+")]
    [InlineData(Key.D0, KeyModifiers.Meta | KeyModifiers.Shift, "+")]
    public void Command_plus_resolves_to_zoom_in(
        Key key,
        KeyModifiers modifiers,
        string? keySymbol) =>
        Assert.Equal(
            DatabaseDiagramKeyboardAction.ZoomIn,
            DatabaseMermaidDiagramView.ResolveKeyboardAction(key, modifiers, keySymbol));

    [Theory]
    [InlineData(Key.OemMinus, KeyModifiers.Meta, "-")]
    [InlineData(Key.Subtract, KeyModifiers.Control, "-")]
    public void Command_minus_resolves_to_zoom_out(
        Key key,
        KeyModifiers modifiers,
        string? keySymbol) =>
        Assert.Equal(
            DatabaseDiagramKeyboardAction.ZoomOut,
            DatabaseMermaidDiagramView.ResolveKeyboardAction(key, modifiers, keySymbol));

    [Fact]
    public void Unmodified_space_resolves_to_fit() =>
        Assert.Equal(
            DatabaseDiagramKeyboardAction.Fit,
            DatabaseMermaidDiagramView.ResolveKeyboardAction(
                Key.Space,
                KeyModifiers.None,
                " "));

    [Theory]
    [InlineData(Key.OemPlus, KeyModifiers.None, "+")]
    [InlineData(Key.OemMinus, KeyModifiers.Meta | KeyModifiers.Shift, "_")]
    [InlineData(Key.Space, KeyModifiers.Meta, " ")]
    [InlineData(Key.OemPlus, KeyModifiers.Meta | KeyModifiers.Alt, "+")]
    public void Similar_keystrokes_do_not_trigger_diagram_navigation(
        Key key,
        KeyModifiers modifiers,
        string? keySymbol) =>
        Assert.Equal(
            DatabaseDiagramKeyboardAction.None,
            DatabaseMermaidDiagramView.ResolveKeyboardAction(key, modifiers, keySymbol));
}
