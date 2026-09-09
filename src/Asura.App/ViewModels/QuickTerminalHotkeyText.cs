using Asura.Core;

namespace Asura.App.ViewModels;

internal enum ShortcutDisplayPlatform
{
    MacOS,
    Windows,
    Linux,
}

internal static class QuickTerminalHotkeyText
{
    public static ShortcutDisplayPlatform CurrentPlatform => OperatingSystem.IsMacOS()
        ? ShortcutDisplayPlatform.MacOS
        : OperatingSystem.IsWindows()
            ? ShortcutDisplayPlatform.Windows
            : ShortcutDisplayPlatform.Linux;

    public static string Example => Format(
        QuickTerminalSettings.Default.Hotkey,
        CurrentPlatform);

    public static string FormatApplicationCommand(
        string key,
        ShortcutDisplayPlatform platform) =>
        platform == ShortcutDisplayPlatform.MacOS ? $"⌘ {key}" : $"Ctrl+{key}";

    public static string FormatApplicationCommand(string key) =>
        FormatApplicationCommand(key, CurrentPlatform);

    public static KeyStroke Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var tokens = text.Split(
            '+',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            throw new FormatException(
                $"Use at least one modifier and one key, for example {Example}.");
        }

        var modifiers = KeyModifiers.None;
        string? key = null;
        foreach (var token in tokens)
        {
            if (TryReadModifier(token, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                throw new FormatException("A Quick Terminal shortcut must contain exactly one key.");
            }

            key = NormalizeKey(token);
        }

        if (modifiers == KeyModifiers.None || key is null)
        {
            throw new FormatException(
                $"Use at least one modifier and one key, for example {Example}.");
        }

        return new KeyStroke(key, modifiers);
    }

    public static string Format(KeyStroke stroke) => Format(stroke, CurrentPlatform);

    public static string Format(KeyStroke stroke, ShortcutDisplayPlatform platform)
    {
        var parts = new List<string>(5);
        if ((stroke.Modifiers & KeyModifiers.Control) != KeyModifiers.None)
        {
            parts.Add(platform == ShortcutDisplayPlatform.MacOS ? "Control" : "Ctrl");
        }

        if ((stroke.Modifiers & KeyModifiers.Alt) != KeyModifiers.None)
        {
            parts.Add(platform == ShortcutDisplayPlatform.MacOS ? "Option" : "Alt");
        }

        if ((stroke.Modifiers & KeyModifiers.Shift) != KeyModifiers.None)
        {
            parts.Add("Shift");
        }

        if ((stroke.Modifiers & KeyModifiers.Meta) != KeyModifiers.None)
        {
            parts.Add(platform switch
            {
                ShortcutDisplayPlatform.MacOS => "Command",
                ShortcutDisplayPlatform.Windows => "Win",
                _ => "Super",
            });
        }

        parts.Add(stroke.Key is "GRAVE" or "OEMTILDE" or "`" ? "`" : stroke.Key);
        return string.Join(" + ", parts);
    }

    private static bool TryReadModifier(string token, out KeyModifiers modifier)
    {
        modifier = token.Trim().ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => KeyModifiers.Control,
            "ALT" or "OPTION" => KeyModifiers.Alt,
            "SHIFT" => KeyModifiers.Shift,
            "CMD" or "COMMAND" or "META" or "SUPER" or "WIN" or "WINDOWS" =>
                KeyModifiers.Meta,
            _ => KeyModifiers.None,
        };
        return modifier != KeyModifiers.None;
    }

    private static string NormalizeKey(string token) => token.Trim().ToUpperInvariant() switch
    {
        "`" or "BACKTICK" or "GRAVE" or "OEMTILDE" => "GRAVE",
        var value when value.Length == 1 => value,
        var value when value.All(character => char.IsAsciiLetterOrDigit(character)) => value,
        _ => throw new FormatException("The shortcut key contains unsupported characters."),
    };
}
