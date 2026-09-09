using Asura.Application;
using Avalonia.Input;
using CoreKeyStroke = Asura.Core.KeyStroke;

namespace Asura.App.Controls;

internal interface IManagedTerminalInputSink
{
    ValueTask SendTextAsync(string text, CancellationToken cancellationToken);

    ValueTask SendKeyAsync(TerminalKeyStroke keyStroke, CancellationToken cancellationToken);

    ValueTask SendPhysicalKeyAsync(
        TerminalPhysicalKeyEvent keyEvent,
        CancellationToken cancellationToken);

    ValueTask SendMouseAsync(TerminalMouseInput mouseInput, CancellationToken cancellationToken);

    ValueTask ScrollViewportAsync(
        TerminalViewportScrollInput scrollInput,
        CancellationToken cancellationToken);

    ValueTask<bool> ClearScrollbackAsync(CancellationToken cancellationToken);

    ValueTask<TerminalFindResult?> FindAsync(
        TerminalFindInput input,
        CancellationToken cancellationToken);

    ValueTask UpdateSelectionAsync(
        TerminalSelectionInput selectionInput,
        CancellationToken cancellationToken);

    ValueTask<TerminalSelectionText> ReadSelectionAsync(CancellationToken cancellationToken);

    ValueTask<TerminalPasteResult> PasteAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken);
}

internal static class ManagedTerminalInput
{
    /// <summary>
    /// Modifier state belongs to the following key event. Sending a standalone
    /// modifier to the terminal engine also counts as terminal input and clears
    /// its selection before a desktop shortcut such as Command+C can read it.
    /// </summary>
    public static bool IsModifierOnly(PhysicalKey physicalKey) => physicalKey is
        PhysicalKey.ShiftLeft
        or PhysicalKey.ShiftRight
        or PhysicalKey.ControlLeft
        or PhysicalKey.ControlRight
        or PhysicalKey.AltLeft
        or PhysicalKey.AltRight
        or PhysicalKey.MetaLeft
        or PhysicalKey.MetaRight;

    public static TerminalPhysicalKeyEvent CreatePhysicalKeyEvent(
        Key logicalKey,
        PhysicalKey physicalKey,
        KeyModifiers modifiers,
        string? keySymbol,
        TerminalKeyAction action,
        bool isComposing)
    {
        var terminalPhysicalKey = MapPhysicalKey(physicalKey);
        var rawText = action == TerminalKeyAction.Release || string.IsNullOrEmpty(keySymbol)
            ? string.Empty
            : keySymbol;
        var unshiftedCodepoint = UnshiftedCodepoint(
            logicalKey,
            physicalKey,
            modifiers,
            rawText);
        var text = NormalizeKeyText(rawText, unshiftedCodepoint);
        var terminalModifiers = MapModifiers(modifiers, physicalKey);
        var consumedModifiers = ConsumedModifiers(
            terminalModifiers,
            text,
            unshiftedCodepoint);
        return new TerminalPhysicalKeyEvent(
            terminalPhysicalKey,
            logicalKey.ToString(),
            text,
            terminalModifiers,
            consumedModifiers,
            action,
            unshiftedCodepoint,
            isComposing);
    }

    public static bool TryMapSpecialKey(
        Key key,
        KeyModifiers modifiers,
        out TerminalKeyStroke? keyStroke)
    {
        var terminalKey = key switch
        {
            Key.Return or Key.Enter => TerminalKey.Enter,
            Key.Tab => TerminalKey.Tab,
            Key.Back => TerminalKey.Backspace,
            Key.Escape => TerminalKey.Escape,
            Key.Space => TerminalKey.Space,
            Key.Up => TerminalKey.Up,
            Key.Down => TerminalKey.Down,
            Key.Left => TerminalKey.Left,
            Key.Right => TerminalKey.Right,
            Key.Home => TerminalKey.Home,
            Key.End => TerminalKey.End,
            Key.PageUp => TerminalKey.PageUp,
            Key.PageDown => TerminalKey.PageDown,
            Key.Insert => TerminalKey.Insert,
            Key.Delete => TerminalKey.Delete,
            >= Key.F1 and <= Key.F20 =>
                (TerminalKey)((int)TerminalKey.F1 + ((int)key - (int)Key.F1)),
            _ => (TerminalKey?)null,
        };
        if (terminalKey is null)
        {
            keyStroke = null;
            return false;
        }

        keyStroke = new TerminalKeyStroke(terminalKey.Value, MapModifiers(modifiers));
        return true;
    }

    public static bool TryEncodeModifiedText(
        string? keySymbol,
        KeyModifiers modifiers,
        out string text)
    {
        text = string.Empty;
        var textModifiers = modifiers
            & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);
        if (string.IsNullOrEmpty(keySymbol)
            || textModifiers == KeyModifiers.None
            || (modifiers.HasFlag(KeyModifiers.Control)
                && modifiers.HasFlag(KeyModifiers.Alt))
            || keySymbol.EnumerateRunes().Count() != 1)
        {
            return false;
        }

        var encoded = keySymbol;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            var character = char.ToUpperInvariant(keySymbol[0]);
            encoded = character switch
            {
                >= 'A' and <= 'Z' => ((char)(character - 'A' + 1)).ToString(),
                '@' or ' ' => "\0",
                '[' => "\u001b",
                '\\' => "\u001c",
                ']' => "\u001d",
                '^' => "\u001e",
                '_' => "\u001f",
                '?' => "\u007f",
                _ => string.Empty,
            };
            if (encoded.Length == 0)
            {
                return false;
            }
        }

        if (modifiers.HasFlag(KeyModifiers.Alt)
            || modifiers.HasFlag(KeyModifiers.Meta))
        {
            encoded = "\u001b" + encoded;
        }

        text = encoded;
        return true;
    }

    public static bool TryMapReplayStroke(
        CoreKeyStroke stroke,
        out TerminalKeyStroke? keyStroke,
        out string text)
    {
        var terminalKey = stroke.Key switch
        {
            "ENTER" or "RETURN" => TerminalKey.Enter,
            "TAB" => TerminalKey.Tab,
            "BACK" or "BACKSPACE" => TerminalKey.Backspace,
            "ESC" or "ESCAPE" => TerminalKey.Escape,
            "SPACE" => TerminalKey.Space,
            "UP" or "ARROWUP" => TerminalKey.Up,
            "DOWN" or "ARROWDOWN" => TerminalKey.Down,
            "LEFT" or "ARROWLEFT" => TerminalKey.Left,
            "RIGHT" or "ARROWRIGHT" => TerminalKey.Right,
            "HOME" => TerminalKey.Home,
            "END" => TerminalKey.End,
            "PAGEUP" => TerminalKey.PageUp,
            "PAGEDOWN" => TerminalKey.PageDown,
            "INSERT" => TerminalKey.Insert,
            "DELETE" => TerminalKey.Delete,
            _ when TryMapFunctionKey(stroke.Key, out var functionKey) => functionKey,
            _ => (TerminalKey?)null,
        };
        if (terminalKey is not null)
        {
            keyStroke = new TerminalKeyStroke(
                terminalKey.Value,
                MapModifiers(stroke.Modifiers));
            text = string.Empty;
            return true;
        }

        keyStroke = null;
        if (stroke.Key.EnumerateRunes().Count() != 1)
        {
            text = string.Empty;
            return false;
        }

        var value = stroke.Key;
        if ((stroke.Modifiers & Asura.Core.KeyModifiers.Shift) == Core.KeyModifiers.None)
        {
            value = value.ToLowerInvariant();
        }

        var characterModifiers = stroke.Modifiers
            & (Asura.Core.KeyModifiers.Control
                | Asura.Core.KeyModifiers.Alt
                | Asura.Core.KeyModifiers.Meta);
        if ((characterModifiers & Asura.Core.KeyModifiers.Control) != Core.KeyModifiers.None)
        {
            var character = char.ToUpperInvariant(value[0]);
            value = character switch
            {
                >= 'A' and <= 'Z' => ((char)(character - 'A' + 1)).ToString(),
                '@' or ' ' => "\0",
                '[' => "\u001b",
                '\\' => "\u001c",
                ']' => "\u001d",
                '^' => "\u001e",
                '_' => "\u001f",
                '?' => "\u007f",
                _ => string.Empty,
            };
            if (value.Length == 0)
            {
                text = string.Empty;
                return false;
            }
        }

        if ((characterModifiers
                & (Asura.Core.KeyModifiers.Alt | Asura.Core.KeyModifiers.Meta)) != Core.KeyModifiers.None)
        {
            value = "\u001b" + value;
        }

        text = value;
        return true;
    }

    public static TerminalKeyModifiers MapModifiers(KeyModifiers modifiers)
        => MapModifiers(modifiers, PhysicalKey.None);

    private static TerminalKeyModifiers MapModifiers(
        KeyModifiers modifiers,
        PhysicalKey physicalKey)
    {
        var result = TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(KeyModifiers.Shift)
            ? TerminalKeyModifiers.Shift
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(KeyModifiers.Alt)
            ? TerminalKeyModifiers.Alt
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(KeyModifiers.Control)
            ? TerminalKeyModifiers.Control
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(KeyModifiers.Meta)
            ? TerminalKeyModifiers.Meta
            : TerminalKeyModifiers.None;
        result |= physicalKey == PhysicalKey.ShiftRight
            && modifiers.HasFlag(KeyModifiers.Shift)
                ? TerminalKeyModifiers.RightShift
                : TerminalKeyModifiers.None;
        result |= physicalKey == PhysicalKey.ControlRight
            && modifiers.HasFlag(KeyModifiers.Control)
                ? TerminalKeyModifiers.RightControl
                : TerminalKeyModifiers.None;
        result |= physicalKey == PhysicalKey.AltRight
            && modifiers.HasFlag(KeyModifiers.Alt)
                ? TerminalKeyModifiers.RightAlt
                : TerminalKeyModifiers.None;
        result |= physicalKey == PhysicalKey.MetaRight
            && modifiers.HasFlag(KeyModifiers.Meta)
                ? TerminalKeyModifiers.RightMeta
                : TerminalKeyModifiers.None;
        return result;
    }

    private static TerminalPhysicalKey MapPhysicalKey(PhysicalKey physicalKey)
    {
        if (physicalKey == PhysicalKey.None)
        {
            return TerminalPhysicalKey.Unidentified;
        }

        return Enum.TryParse<TerminalPhysicalKey>(physicalKey.ToString(), out var mapped)
            ? mapped
            : TerminalPhysicalKey.Unidentified;
    }

    private static uint UnshiftedCodepoint(
        Key logicalKey,
        PhysicalKey physicalKey,
        KeyModifiers modifiers,
        string text)
    {
        var textRunes = text.EnumerateRunes().Take(2).ToArray();
        if (textRunes is [var textRune]
            && textRune.Value >= 0x20
            && textRune.Value is not (>= 0xF700 and <= 0xF8FF)
            && !modifiers.HasFlag(KeyModifiers.Control)
            && !modifiers.HasFlag(KeyModifiers.Alt)
            && !modifiers.HasFlag(KeyModifiers.Meta))
        {
            // Avalonia's logical Key remains Latin on layouts such as JCUKEN.
            // Prefer the platform-produced symbol for unmodified alphabetic
            // input so Kitty alternate-key reporting preserves that layout.
            // Shifted punctuation still needs the physical fallback below.
            if (!modifiers.HasFlag(KeyModifiers.Shift)
                || System.Text.Rune.IsLetter(textRune))
            {
                return checked((uint)System.Text.Rune.ToLowerInvariant(textRune).Value);
            }
        }

        if (logicalKey is >= Key.A and <= Key.Z)
        {
            return checked((uint)('a' + logicalKey - Key.A));
        }

        var qwerty = physicalKey.ToQwertyKeySymbol(useShiftModifier: false);
        if (!string.IsNullOrEmpty(qwerty)
            && qwerty.EnumerateRunes().Take(2).ToArray() is [var qwertyRune])
        {
            return checked((uint)qwertyRune.Value);
        }

        return textRunes is [var rune]
                ? checked((uint)System.Text.Rune.ToLowerInvariant(rune).Value)
                : 0;
    }

    private static string NormalizeKeyText(string text, uint unshiftedCodepoint)
    {
        if (text.EnumerateRunes().Take(2).ToArray() is not [var rune])
        {
            return text;
        }

        // Match the terminal adapter contract: control characters are encoded by
        // the key encoder from the unmodified character plus modifiers, while
        // AppKit's private-use function-key scalars are never terminal text.
        if (rune.Value < 0x20)
        {
            return System.Text.Rune.IsValid(checked((int)unshiftedCodepoint))
                && unshiftedCodepoint != 0
                    ? new System.Text.Rune(checked((int)unshiftedCodepoint)).ToString()
                    : string.Empty;
        }

        return rune.Value is >= 0xF700 and <= 0xF8FF
            ? string.Empty
            : text;
    }

    private static TerminalKeyModifiers ConsumedModifiers(
        TerminalKeyModifiers modifiers,
        string text,
        uint unshiftedCodepoint)
    {
        if (text.Length == 0)
        {
            return TerminalKeyModifiers.None;
        }

        var consumed = TerminalKeyModifiers.None;
        var runes = text.EnumerateRunes().Take(2).ToArray();
        if (modifiers.HasFlag(TerminalKeyModifiers.Shift)
            && runes is [var rune]
            && rune.Value != unshiftedCodepoint)
        {
            consumed |= TerminalKeyModifiers.Shift;
            if (modifiers.HasFlag(TerminalKeyModifiers.RightShift))
            {
                consumed |= TerminalKeyModifiers.RightShift;
            }
        }

        var usesAltGr = modifiers.HasFlag(TerminalKeyModifiers.Control)
            && modifiers.HasFlag(TerminalKeyModifiers.Alt);
        if (usesAltGr)
        {
            consumed |= TerminalKeyModifiers.Control | TerminalKeyModifiers.Alt;
            if (modifiers.HasFlag(TerminalKeyModifiers.RightControl))
            {
                consumed |= TerminalKeyModifiers.RightControl;
            }

            if (modifiers.HasFlag(TerminalKeyModifiers.RightAlt))
            {
                consumed |= TerminalKeyModifiers.RightAlt;
            }
        }
        else if (modifiers.HasFlag(TerminalKeyModifiers.Alt)
            && runes is [var altRune]
            && altRune.Value != unshiftedCodepoint)
        {
            // macOS Option and similar layout modifiers are consumed when they
            // translated the key into text. Plain Alt+A on platforms where Alt
            // is a terminal modifier remains unconsumed because the text is "a".
            consumed |= TerminalKeyModifiers.Alt;
            if (modifiers.HasFlag(TerminalKeyModifiers.RightAlt))
            {
                consumed |= TerminalKeyModifiers.RightAlt;
            }
        }

        return consumed;
    }

    private static TerminalKeyModifiers MapModifiers(Asura.Core.KeyModifiers modifiers)
    {
        var result = TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(Asura.Core.KeyModifiers.Shift)
            ? TerminalKeyModifiers.Shift
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(Asura.Core.KeyModifiers.Alt)
            ? TerminalKeyModifiers.Alt
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(Asura.Core.KeyModifiers.Control)
            ? TerminalKeyModifiers.Control
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(Asura.Core.KeyModifiers.Meta)
            ? TerminalKeyModifiers.Meta
            : TerminalKeyModifiers.None;
        return result;
    }

    private static bool TryMapFunctionKey(string key, out TerminalKey terminalKey)
    {
        if (key.Length is >= 2 and <= 3
            && key[0] == 'F'
            && int.TryParse(
                key.AsSpan(1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number)
            && number is >= 1 and <= 20)
        {
            terminalKey = (TerminalKey)((int)TerminalKey.F1 + number - 1);
            return true;
        }

        terminalKey = default;
        return false;
    }

    public static bool TryMapScrollShortcut(
        Key key,
        KeyModifiers modifiers,
        int pageLines,
        out TerminalViewportScrollInput? scrollInput)
    {
        if ((modifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta))
            != KeyModifiers.Shift)
        {
            scrollInput = null;
            return false;
        }

        var lines = key switch
        {
            Key.PageUp => -Math.Max(1, pageLines),
            Key.PageDown => Math.Max(1, pageLines),
            _ => 0,
        };
        scrollInput = lines == 0 ? null : new TerminalViewportScrollInput(lines);
        return scrollInput is not null;
    }
}
