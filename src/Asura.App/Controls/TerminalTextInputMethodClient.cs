using Avalonia;
using Avalonia.Input.TextInput;

namespace Asura.App.Controls;

/// <summary>
/// Supplies the platform IME with the terminal cursor rectangle and a preedit surface.
/// Committed text still arrives through Avalonia's TextInput event and follows the normal
/// application input lease.
/// </summary>
internal sealed class TerminalTextInputMethodClient(ManagedTerminalSurface surface)
    : TextInputMethodClient
{
    private readonly ManagedTerminalSurface _surface =
        surface ?? throw new ArgumentNullException(nameof(surface));

    public override Visual TextViewVisual => _surface;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => false;

    public override string SurroundingText => string.Empty;

    public override Rect CursorRectangle => _surface.GetImeCursorRectangle();

    public override TextSelection Selection
    {
        get => new(0, 0);
        set => _ = value;
    }

    public override void SetPreeditText(string? preeditText, int? cursorPos) =>
        _surface.UpdatePreedit(preeditText, cursorPos);

    public override void SetPreeditText(string? preeditText) =>
        _surface.UpdatePreedit(preeditText, null);

    public void NotifyCursorRectangleChanged() => RaiseCursorRectangleChanged();

    public void NotifyTextViewChanged() => RaiseTextViewVisualChanged();
}
