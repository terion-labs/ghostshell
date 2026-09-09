using System.Globalization;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Renders a key sequence for people rather than for comparison.
/// <see cref="KeyStroke.Key"/> is canonically upper-case so strokes compare
/// exactly, which makes the stored form read as <c>Ctrl+B, ARROWLEFT</c>. This
/// maps the shouted names onto the symbols and casing a keyboard actually shows.
/// </summary>
public static class KeySequenceDisplay
{
    private static readonly Dictionary<string, string> KeyLabels = new(StringComparer.Ordinal)
    {
        ["ARROWLEFT"] = "←",
        ["ARROWRIGHT"] = "→",
        ["ARROWUP"] = "↑",
        ["ARROWDOWN"] = "↓",
        ["LEFT"] = "←",
        ["RIGHT"] = "→",
        ["UP"] = "↑",
        ["DOWN"] = "↓",
        ["PAGEUP"] = "Page Up",
        ["PAGEDOWN"] = "Page Down",
        ["ESCAPE"] = "Esc",
        ["ENTER"] = "Enter",
        ["RETURN"] = "Enter",
        ["SPACE"] = "Space",
        ["TAB"] = "Tab",
        ["BACKSPACE"] = "Backspace",
        ["DELETE"] = "Delete",
        ["INSERT"] = "Insert",
        ["HOME"] = "Home",
        ["END"] = "End",
        ["GRAVE"] = "`",
        ["OEMTILDE"] = "`",
        ["OEMCOMMA"] = ",",
        ["OEMPERIOD"] = ".",
        ["OEMMINUS"] = "-",
        ["OEMPLUS"] = "=",
        ["OEMOPENBRACKETS"] = "[",
        ["OEMCLOSEBRACKETS"] = "]",
        ["OEMQUESTION"] = "/",
        ["OEMPIPE"] = "\\",
        ["OEMSEMICOLON"] = ";",
        ["OEMQUOTES"] = "'",
    };

    public static string Format(KeySequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return string.Join(", ", sequence.Strokes.Select(Format));
    }

    public static string Format(KeyStroke stroke)
    {
        var parts = new List<string>(5);
        if ((stroke.Modifiers & KeyModifiers.Control) != KeyModifiers.None)
        {
            parts.Add("Ctrl");
        }

        if ((stroke.Modifiers & KeyModifiers.Alt) != KeyModifiers.None)
        {
            parts.Add(OperatingSystem.IsMacOS() ? "Option" : "Alt");
        }

        if ((stroke.Modifiers & KeyModifiers.Shift) != KeyModifiers.None)
        {
            parts.Add("Shift");
        }

        if ((stroke.Modifiers & KeyModifiers.Meta) != KeyModifiers.None)
        {
            parts.Add(OperatingSystem.IsMacOS()
                ? "Cmd"
                : OperatingSystem.IsWindows() ? "Win" : "Super");
        }

        parts.Add(KeyLabel(stroke.Key));
        return string.Join('+', parts);
    }

    private static string KeyLabel(string key)
    {
        if (KeyLabels.TryGetValue(key, out var label))
        {
            return label;
        }

        // Single characters and function keys already read correctly upper-case;
        // anything else is a word that should not shout.
        if (key.Length <= 1 || IsFunctionKey(key))
        {
            return key;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.ToLowerInvariant());
    }

    private static bool IsFunctionKey(string key) =>
        key.Length is 2 or 3
        && key[0] == 'F'
        && key[1..].All(char.IsAsciiDigit);
}
