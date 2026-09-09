using System.Text;

namespace Asura.Core;

/// <summary>
/// Defines the durable key names that every desktop terminal renderer can resolve.
/// Application keymaps are intentionally not limited by this contract.
/// </summary>
public static class TerminalKeyBindingRules
{
    private static readonly HashSet<string> SupportedNamedKeys = new(StringComparer.Ordinal)
    {
        "ARROWLEFT",
        "ARROWRIGHT",
        "ARROWUP",
        "ARROWDOWN",
        "ENTER",
        "TAB",
        "BACKSPACE",
        "ESCAPE",
        "SPACE",
        "HOME",
        "END",
        "PAGEUP",
        "PAGEDOWN",
        "INSERT",
        "DELETE",
        "OEMSEMICOLON",
        "OEM1",
        "OEMPLUS",
        "OEMCOMMA",
        "OEMMINUS",
        "OEMPERIOD",
        "OEMQUESTION",
        "OEM2",
        "OEMTILDE",
        "OEM3",
        "OEMOPENBRACKETS",
        "OEM4",
        "OEMPIPE",
        "OEM5",
        "OEMCLOSEBRACKETS",
        "OEM6",
        "OEMQUOTES",
        "OEM7",
        "OEMBACKSLASH",
        "OEM102",
    };

    public static bool IsSupported(KeyStroke stroke)
    {
        var key = stroke.Key;
        if (SupportedNamedKeys.Contains(key))
        {
            return true;
        }

        if (key.Length is >= 2 and <= 3
            && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture, out var functionKey) && functionKey is >= 1 and <= 20)
        {
            return true;
        }

        var runes = key.EnumerateRunes().ToArray();
        return runes.Length == 1
            && !Rune.IsControl(runes[0])
            && runes[0].Value is not '\r' and not '\n' and not '>';
    }
}
