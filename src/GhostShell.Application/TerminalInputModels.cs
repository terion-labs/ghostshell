using GhostShell.Core;

namespace GhostShell.Application;

public enum TerminalKey
{
    Enter,
    Tab,
    Backspace,
    Escape,
    Space,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13,
    F14,
    F15,
    F16,
    F17,
    F18,
    F19,
    F20,
}

[Flags]
public enum TerminalKeyModifiers
{
    None = 0,
    Shift = 1 << 0,
    Alt = 1 << 1,
    Control = 1 << 2,
    Meta = 1 << 3,
    CapsLock = 1 << 4,
    NumLock = 1 << 5,
    RightShift = 1 << 6,
    RightControl = 1 << 7,
    RightAlt = 1 << 8,
    RightMeta = 1 << 9,
}

public enum TerminalKeyAction
{
    Release,
    Press,
    Repeat,
}

/// <summary>
/// Layout-independent keyboard positions understood by the terminal engine.
/// Names follow the W3C UI Events <c>code</c> values used by both Avalonia and
/// Ghostty. The terminal adapter owns the pinned native mapping; callers must
/// treat these values as symbolic application data rather than native integers.
/// </summary>
public enum TerminalPhysicalKey
{
    Unidentified,
    Backquote,
    Backslash,
    BracketLeft,
    BracketRight,
    Comma,
    Digit0,
    Digit1,
    Digit2,
    Digit3,
    Digit4,
    Digit5,
    Digit6,
    Digit7,
    Digit8,
    Digit9,
    Equal,
    IntlBackslash,
    IntlRo,
    IntlYen,
    A,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,
    Minus,
    Period,
    Quote,
    Semicolon,
    Slash,
    AltLeft,
    AltRight,
    Backspace,
    CapsLock,
    ContextMenu,
    ControlLeft,
    ControlRight,
    Enter,
    MetaLeft,
    MetaRight,
    ShiftLeft,
    ShiftRight,
    Space,
    Tab,
    Convert,
    KanaMode,
    NonConvert,
    Delete,
    End,
    Help,
    Home,
    Insert,
    PageDown,
    PageUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    ArrowUp,
    NumLock,
    NumPad0,
    NumPad1,
    NumPad2,
    NumPad3,
    NumPad4,
    NumPad5,
    NumPad6,
    NumPad7,
    NumPad8,
    NumPad9,
    NumPadAdd,
    NumPadBackspace,
    NumPadClear,
    NumPadClearEntry,
    NumPadComma,
    NumPadDecimal,
    NumPadDivide,
    NumPadEnter,
    NumPadEqual,
    NumPadMemoryAdd,
    NumPadMemoryClear,
    NumPadMemoryRecall,
    NumPadMemoryStore,
    NumPadMemorySubtract,
    NumPadMultiply,
    NumPadParenLeft,
    NumPadParenRight,
    NumPadSubtract,
    NumPadSeparator,
    NumPadUp,
    NumPadDown,
    NumPadRight,
    NumPadLeft,
    NumPadBegin,
    NumPadHome,
    NumPadEnd,
    NumPadInsert,
    NumPadDelete,
    NumPadPageUp,
    NumPadPageDown,
    Escape,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13,
    F14,
    F15,
    F16,
    F17,
    F18,
    F19,
    F20,
    F21,
    F22,
    F23,
    F24,
    F25,
    Fn,
    FnLock,
    PrintScreen,
    ScrollLock,
    Pause,
    BrowserBack,
    BrowserFavorites,
    BrowserForward,
    BrowserHome,
    BrowserRefresh,
    BrowserSearch,
    BrowserStop,
    Eject,
    LaunchApp1,
    LaunchApp2,
    LaunchMail,
    MediaPlayPause,
    MediaSelect,
    MediaStop,
    MediaTrackNext,
    MediaTrackPrevious,
    Power,
    Sleep,
    AudioVolumeDown,
    AudioVolumeMute,
    AudioVolumeUp,
    WakeUp,
    Copy,
    Cut,
    Paste,
}

/// <summary>
/// One platform keyboard event before terminal-protocol encoding.
/// </summary>
/// <remarks>
/// <see cref="Text"/> is managed Unicode text and is converted to UTF-8 only at
/// the libghostty-vt boundary. Committed IME text does not use this type: it is
/// intentionally delivered through the direct text input port after composition
/// has finished.
/// </remarks>
public sealed record TerminalPhysicalKeyEvent
{
    public TerminalPhysicalKeyEvent(
        TerminalPhysicalKey PhysicalKey,
        string LogicalKey,
        string Text,
        TerminalKeyModifiers Modifiers,
        TerminalKeyModifiers ConsumedModifiers,
        TerminalKeyAction Action,
        uint UnshiftedCodepoint = 0,
        bool IsComposing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(LogicalKey);
        ArgumentNullException.ThrowIfNull(Text);
        if (!Enum.IsDefined(PhysicalKey))
        {
            throw new ArgumentOutOfRangeException(nameof(PhysicalKey));
        }

        if (!Enum.IsDefined(Action))
        {
            throw new ArgumentOutOfRangeException(nameof(Action));
        }

        if ((Modifiers & ~AllKeyboardModifiers) != TerminalKeyModifiers.None)
        {
            throw new ArgumentOutOfRangeException(nameof(Modifiers));
        }

        if ((ConsumedModifiers & ~Modifiers) != TerminalKeyModifiers.None)
        {
            throw new ArgumentException(
                "Consumed modifiers must also be present in the keyboard event.",
                nameof(ConsumedModifiers));
        }

        if (UnshiftedCodepoint > 0x10FFFF
            || UnshiftedCodepoint is >= 0xD800 and <= 0xDFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(UnshiftedCodepoint));
        }

        this.PhysicalKey = PhysicalKey;
        this.LogicalKey = LogicalKey;
        this.Text = Text;
        this.Modifiers = Modifiers;
        this.ConsumedModifiers = ConsumedModifiers;
        this.Action = Action;
        this.UnshiftedCodepoint = UnshiftedCodepoint;
        this.IsComposing = IsComposing;
    }

    public TerminalPhysicalKey PhysicalKey { get; }

    public string LogicalKey { get; }

    public string Text { get; }

    public TerminalKeyModifiers Modifiers { get; }

    public TerminalKeyModifiers ConsumedModifiers { get; }

    public TerminalKeyAction Action { get; }

    public uint UnshiftedCodepoint { get; }

    public bool IsComposing { get; }

    internal const TerminalKeyModifiers AllKeyboardModifiers =
        TerminalKeyModifiers.Shift
        | TerminalKeyModifiers.Alt
        | TerminalKeyModifiers.Control
        | TerminalKeyModifiers.Meta
        | TerminalKeyModifiers.CapsLock
        | TerminalKeyModifiers.NumLock
        | TerminalKeyModifiers.RightShift
        | TerminalKeyModifiers.RightControl
        | TerminalKeyModifiers.RightAlt
        | TerminalKeyModifiers.RightMeta;
}

public sealed record TerminalKeyStroke
{
    public const int MaximumRepeatCount = 64;

    public TerminalKeyStroke(
        TerminalKey Key,
        TerminalKeyModifiers Modifiers = TerminalKeyModifiers.None,
        int RepeatCount = 1)
    {
        if (!Enum.IsDefined(Key))
        {
            throw new ArgumentOutOfRangeException(nameof(Key));
        }

        if ((Modifiers & ~AllModifiers) != TerminalKeyModifiers.None)
        {
            throw new ArgumentOutOfRangeException(nameof(Modifiers));
        }

        if (RepeatCount is < 1 or > MaximumRepeatCount)
        {
            throw new ArgumentOutOfRangeException(nameof(RepeatCount));
        }

        this.Key = Key;
        this.Modifiers = Modifiers;
        this.RepeatCount = RepeatCount;
    }

    public TerminalKey Key { get; }

    public TerminalKeyModifiers Modifiers { get; }

    /// <summary>
    /// The bounded number of identical key presses delivered as one terminal
    /// input operation.
    /// </summary>
    public int RepeatCount { get; }

    private const TerminalKeyModifiers AllModifiers =
        TerminalPhysicalKeyEvent.AllKeyboardModifiers;
}

public enum TerminalCharacterChordModifier
{
    Control,
    Alt,
}

/// <summary>
/// A bounded physical-style terminal character chord. Character casing is
/// canonical: the lowercase value names the key and does not imply Shift.
/// </summary>
public sealed record TerminalCharacterChord
{
    public TerminalCharacterChord(
        char Character,
        TerminalCharacterChordModifier Modifier)
    {
        if (Character is < 'a' or > 'z')
        {
            throw new ArgumentOutOfRangeException(
                nameof(Character),
                Character,
                "A terminal character chord requires one lowercase ASCII letter.");
        }

        if (!Enum.IsDefined(Modifier))
        {
            throw new ArgumentOutOfRangeException(nameof(Modifier));
        }

        this.Character = Character;
        this.Modifier = Modifier;
    }

    public char Character { get; }

    public TerminalCharacterChordModifier Modifier { get; }
}

public enum TerminalMouseButton
{
    None,
    Left,
    Middle,
    Right,
    WheelUp,
    WheelDown,
}

public enum TerminalMouseEventKind
{
    Down,
    Up,
    Move,
    Drag,
    WheelUp,
    WheelDown,
}

public sealed record TerminalMouseInput
{
    public TerminalMouseInput(
        TerminalMouseButton Button,
        TerminalMouseEventKind Kind,
        int Column,
        int Row,
        TerminalKeyModifiers Modifiers = TerminalKeyModifiers.None)
    {
        if (!Enum.IsDefined(Button))
        {
            throw new ArgumentOutOfRangeException(nameof(Button));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (!IsSupportedEvent(Button, Kind))
        {
            throw new ArgumentException(
                "The terminal mouse button and event kind do not form a supported event.",
                nameof(Kind));
        }

        if (Column is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Column));
        }

        if (Row is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Row));
        }

        if ((Modifiers & ~(TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Control | TerminalKeyModifiers.Meta)) != TerminalKeyModifiers.None)
        {
            throw new ArgumentOutOfRangeException(nameof(Modifiers));
        }

        this.Button = Button;
        this.Kind = Kind;
        this.Column = Column;
        this.Row = Row;
        this.Modifiers = Modifiers;
    }

    public TerminalMouseButton Button { get; }

    public TerminalMouseEventKind Kind { get; }

    public int Column { get; }

    public int Row { get; }

    public TerminalKeyModifiers Modifiers { get; }

    private static bool IsSupportedEvent(
        TerminalMouseButton button,
        TerminalMouseEventKind kind) =>
        (button, kind) switch
        {
            (TerminalMouseButton.None, TerminalMouseEventKind.Move) => true,
            (
                TerminalMouseButton.Left
                    or TerminalMouseButton.Middle
                    or TerminalMouseButton.Right,
                TerminalMouseEventKind.Down
                    or TerminalMouseEventKind.Up
                    or TerminalMouseEventKind.Drag) => true,
            (TerminalMouseButton.WheelUp, TerminalMouseEventKind.WheelUp) => true,
            (TerminalMouseButton.WheelDown, TerminalMouseEventKind.WheelDown) => true,
            _ => false,
        };
}

public enum TerminalRevisionBoundMouseOutcome
{
    Sent,
    ContentRevisionChanged,
    CoordinatesOutOfBounds,
    MouseTrackingDisabled,
}

public sealed record TerminalPasteInput
{
    public const int MaximumCharacters = 4 * 1024 * 1024;

    public TerminalPasteInput(string Text)
    {
        ArgumentNullException.ThrowIfNull(Text);
        if (Text.Length > MaximumCharacters)
        {
            throw new ArgumentException(
                $"A terminal paste cannot exceed {MaximumCharacters} characters.",
                nameof(Text));
        }

        this.Text = Text;
    }

    public string Text { get; }
}

public sealed record TerminalPasteResult(
    bool Sent,
    bool RequiresConfirmation,
    bool UsedBracketedPaste,
    string? Detail)
{
    public static TerminalPasteResult ConfirmationRequired(bool bracketed) => new(
        false,
        true,
        bracketed,
        "The terminal requires confirmation before accepting this paste.");

    public static TerminalPasteResult Completed(bool bracketed) => new(
        true,
        false,
        bracketed,
        null);
}

public sealed record TerminalKeyRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    TerminalKeyStroke KeyStroke);

public sealed record TerminalPhysicalKeyRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    TerminalPhysicalKeyEvent KeyEvent);

public sealed record TerminalMouseRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    TerminalMouseInput MouseInput);

public sealed record TerminalPasteRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    TerminalPasteInput PasteInput);
