using Asura.App.Controls;
using Asura.Core;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views;

public sealed partial class ShortcutRecorderDialog : Window
{
    public const int DefaultMaximumStrokes = 8;
    private readonly List<KeyStroke> _strokes = [];
    private readonly int _maximumStrokes;

    public ShortcutRecorderDialog()
        : this(initial: null, DefaultMaximumStrokes)
    {
    }

    public ShortcutRecorderDialog(KeySequence? initial)
        : this(initial, DefaultMaximumStrokes)
    {
    }

    public ShortcutRecorderDialog(KeySequence? initial, int maximumStrokes)
    {
        if (maximumStrokes is < 1 or > DefaultMaximumStrokes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumStrokes),
                $"Shortcut capture supports between 1 and {DefaultMaximumStrokes} strokes.");
        }

        _maximumStrokes = maximumStrokes;
        InitializeComponent();
        Opened += (_, _) => this.FindControl<SurfaceCard>("CaptureArea")?.Focus();
        if (initial is not null)
        {
            _strokes.AddRange(initial.Strokes);
        }

        Refresh();
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
            return;
        }

        if (IsModifierKey(e.Key))
        {
            this.FindControl<TextBlock>("RecorderStatusText")!.Text =
                "Add a non-modifier key to complete the stroke.";
            e.Handled = true;
            return;
        }

        if (_strokes.Count >= _maximumStrokes)
        {
            this.FindControl<TextBlock>("RecorderStatusText")!.Text =
                _maximumStrokes == 1
                    ? "Terminal shortcuts contain exactly one key stroke."
                    : $"A shortcut cannot contain more than {_maximumStrokes} strokes.";
            e.Handled = true;
            return;
        }

        _strokes.Add(ApplicationKeyStrokeMapper.Map(e.Key, e.KeyModifiers, e.KeySymbol));
        Refresh();
        e.Handled = true;
    }

    private void OnRemoveLastClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_strokes.Count > 0)
        {
            _strokes.RemoveAt(_strokes.Count - 1);
            Refresh();
        }
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _strokes.Clear();
        Refresh();
        this.FindControl<SurfaceCard>("CaptureArea")?.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private void OnUseShortcutClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(_strokes.Count is 0 || _strokes.Count > _maximumStrokes
            ? null
            : new KeySequence(_strokes));
    }

    private void Refresh()
    {
        var hasStrokes = _strokes.Count > 0;
        this.FindControl<TextBlock>("RecordedShortcutText")!.Text = hasStrokes
            ? string.Join(", ", _strokes)
            : "No keys recorded";
        this.FindControl<TextBlock>("StrokeCountText")!.Text =
            $"{_strokes.Count} of {_maximumStrokes} {(_maximumStrokes == 1 ? "stroke" : "strokes")}";
        this.FindControl<DialogShell>("Shell")!.Subtitle = _maximumStrokes == 1
            ? "Record one terminal stroke. Conflicts are checked before the profile can be saved."
            : $"Record up to {_maximumStrokes} strokes. Conflicts are checked before the profile can be saved.";
        this.FindControl<TextBlock>("RecorderStatusText")!.Text =
            _strokes.Count > _maximumStrokes
                ? $"This imported shortcut has too many strokes. Remove {_strokes.Count - _maximumStrokes} to repair it."
                : _strokes.Count == _maximumStrokes
                    ? "The shortcut is ready to use."
                    : "Focus the capture area to add another stroke.";
        this.FindControl<Button>("RemoveLastButton")!.IsEnabled = hasStrokes;
        this.FindControl<Button>("ClearButton")!.IsEnabled = hasStrokes;
        this.FindControl<Button>("UseShortcutButton")!.IsEnabled =
            hasStrokes && _strokes.Count <= _maximumStrokes;
    }

    private static bool IsModifierKey(Key key) => key.ToString() is
        "LeftCtrl" or "RightCtrl"
        or "LeftAlt" or "RightAlt"
        or "LeftShift" or "RightShift"
        or "LWin" or "RWin";
}
