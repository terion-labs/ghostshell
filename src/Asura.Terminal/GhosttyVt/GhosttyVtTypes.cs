using System.Runtime.InteropServices;

namespace Asura.Terminal.GhosttyVt;

// ABI source: Ghostty include/ghostty/vt at the repository-pinned revision.
// Ghostty explicitly keeps its enums backed by C `int`; do not narrow these.

internal enum GhosttyVtResult : int
{
    Success = 0,
    OutOfMemory = -1,
    InvalidValue = -2,
    OutOfSpace = -3,
    NoValue = -4,
}

internal enum GhosttyVtBuildInfo : int
{
    Invalid = 0,
    Simd = 1,
    KittyGraphics = 2,
    TmuxControlMode = 3,
    Optimize = 4,
    VersionString = 5,
    VersionMajor = 6,
    VersionMinor = 7,
    VersionPatch = 8,
    VersionPre = 9,
    VersionBuild = 10,
}

internal enum GhosttyVtOptimizeMode : int
{
    Debug = 0,
    ReleaseSafe = 1,
    ReleaseSmall = 2,
    ReleaseFast = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct GhosttyVtString
{
    internal GhosttyVtString(nint pointer, nuint length)
    {
        Pointer = pointer;
        Length = length;
    }

    internal readonly nint Pointer;
    internal readonly nuint Length;

    internal string CopyUtf8() =>
        Pointer == 0 || Length == 0
            ? string.Empty
            : Marshal.PtrToStringUTF8(Pointer, checked((int)Length)) ?? string.Empty;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtBuffer
{
    internal nint Pointer;
    internal nuint Capacity;
    internal nuint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct GhosttyVtColorRgb
{
    internal GhosttyVtColorRgb(byte red, byte green, byte blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    internal readonly byte Red;
    internal readonly byte Green;
    internal readonly byte Blue;
}

internal enum GhosttyVtStyleColorTag : int
{
    None = 0,
    Palette = 1,
    Rgb = 2,
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct GhosttyVtStyleColorValue
{
    [FieldOffset(0)] internal byte Palette;
    [FieldOffset(0)] internal GhosttyVtColorRgb Rgb;
    [FieldOffset(0)] private readonly ulong _padding;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtStyleColor
{
    internal GhosttyVtStyleColorTag Tag;
    internal GhosttyVtStyleColorValue Value;
}

internal enum GhosttyVtUnderlineStyle : int
{
    None = 0,
    Single = 1,
    Double = 2,
    Curly = 3,
    Dotted = 4,
    Dashed = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtStyle
{
    internal nuint Size;
    internal GhosttyVtStyleColor Foreground;
    internal GhosttyVtStyleColor Background;
    internal GhosttyVtStyleColor UnderlineColor;
    internal byte Bold;
    internal byte Italic;
    internal byte Faint;
    internal byte Blink;
    internal byte Inverse;
    internal byte Invisible;
    internal byte Strikethrough;
    internal byte Overline;
    internal GhosttyVtUnderlineStyle Underline;

    internal static GhosttyVtStyle CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtStyle>()),
    };
}

internal enum GhosttyVtTerminalScreen : int
{
    Primary = 0,
    Alternate = 1,
}

internal enum GhosttyVtTerminalCursorStyle : int
{
    Bar = 0,
    Block = 1,
    Underline = 2,
    HollowBlock = 3,
}

internal enum GhosttyVtTerminalOption : int
{
    UserData = 0,
    WritePty = 1,
    Bell = 2,
    Enquiry = 3,
    XtVersion = 4,
    TitleChanged = 5,
    Size = 6,
    ColorScheme = 7,
    DeviceAttributes = 8,
    Title = 9,
    WorkingDirectory = 10,
    ForegroundColor = 11,
    BackgroundColor = 12,
    CursorColor = 13,
    ColorPalette = 14,
    KittyImageStorageLimit = 15,
    KittyImageMediumFile = 16,
    KittyImageMediumTemporaryFile = 17,
    KittyImageMediumSharedMemory = 18,
    ApcMaximumBytes = 19,
    ApcMaximumKittyBytes = 20,
    Selection = 21,
    DefaultCursorStyle = 22,
    DefaultCursorBlink = 23,
    GlyphProtocol = 24,
    WorkingDirectoryChanged = 25,
    ClipboardWrite = 26,
    ScrollbackMaximumBytes = 27,
    ScrollbackMaximumLines = 28,
    DesktopNotification = 29,
    ProgressReport = 30,
    SemanticPrompt = 31,
}

internal enum GhosttyVtSemanticPromptPhase : int
{
    Prompt = 0,
    Input = 1,
    Executed = 2,
    Finished = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtSemanticPromptEvent
{
    internal nuint Size;
    internal GhosttyVtSemanticPromptPhase Phase;
    internal byte HasExitStatus;
    internal int ExitStatus;
}

/// <summary>
/// What OSC 9 or OSC 777 asked to be shown.
///
/// A sized struct: <see cref="Size"/> is the byte count the library wrote, and
/// a field beyond it belongs to a newer library than the one that produced this
/// event. Both strings are borrowed for the duration of the callback only, so
/// they must be copied before returning.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtDesktopNotificationEvent
{
    internal nuint Size;
    internal GhosttyVtString Title;
    internal GhosttyVtString Body;
}

internal enum GhosttyVtTerminalData : int
{
    Invalid = 0,
    Columns = 1,
    Rows = 2,
    CursorX = 3,
    CursorY = 4,
    CursorPendingWrap = 5,
    ActiveScreen = 6,
    CursorVisible = 7,
    KittyKeyboardFlags = 8,
    Scrollbar = 9,
    CursorStyle = 10,
    MouseTracking = 11,
    Title = 12,
    WorkingDirectory = 13,
    TotalRows = 14,
    ScrollbackRows = 15,
    WidthPixels = 16,
    HeightPixels = 17,
    ForegroundColor = 18,
    BackgroundColor = 19,
    CursorColor = 20,
    ColorPalette = 21,
    DefaultForegroundColor = 22,
    DefaultBackgroundColor = 23,
    DefaultCursorColor = 24,
    DefaultColorPalette = 25,
    KittyImageStorageLimit = 26,
    KittyImageMediumFile = 27,
    KittyImageMediumTemporaryFile = 28,
    KittyImageMediumSharedMemory = 29,
    KittyGraphics = 30,
    Selection = 31,
    ViewportActive = 32,
    VtProcessingError = 33,
    ScrollbackMaximumBytes = 34,
    ScrollbackMaximumLines = 35,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtTerminalScrollbar
{
    internal ulong Total;
    internal ulong Offset;
    internal ulong Length;
}

internal enum GhosttyVtScrollViewportTag : int
{
    Top = 0,
    Bottom = 1,
    Delta = 2,
    Row = 3,
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct GhosttyVtScrollViewportValue
{
    [FieldOffset(0)] internal nint Delta;
    [FieldOffset(0)] internal nuint Row;
    [FieldOffset(0)] private readonly ulong _padding0;
    [FieldOffset(8)] private readonly ulong _padding1;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct GhosttyVtScrollViewport
{
    [FieldOffset(0)] internal GhosttyVtScrollViewportTag Tag;
    [FieldOffset(8)] internal GhosttyVtScrollViewportValue Value;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct GhosttyVtMode
{
    internal GhosttyVtMode(ushort value) => Value = value;

    internal readonly ushort Value;
}

internal enum GhosttyVtRenderStateDirty : int
{
    Clean = 0,
    Partial = 1,
    Full = 2,
}

internal enum GhosttyVtRenderCursorStyle : int
{
    Bar = 0,
    Block = 1,
    Underline = 2,
    HollowBlock = 3,
}

internal enum GhosttyVtRenderStateData : int
{
    Invalid = 0,
    Columns = 1,
    Rows = 2,
    Dirty = 3,
    RowIterator = 4,
    BackgroundColor = 5,
    ForegroundColor = 6,
    CursorColor = 7,
    CursorColorHasValue = 8,
    ColorPalette = 9,
    CursorVisualStyle = 10,
    CursorVisible = 11,
    CursorBlinking = 12,
    CursorPasswordInput = 13,
    CursorViewportHasValue = 14,
    CursorViewportX = 15,
    CursorViewportY = 16,
    CursorViewportWideTail = 17,
}

internal enum GhosttyVtRenderStateOption : int
{
    Dirty = 0,
}

internal enum GhosttyVtRenderRowData : int
{
    Invalid = 0,
    Dirty = 1,
    Raw = 2,
    Cells = 3,
    Selection = 4,
}

internal enum GhosttyVtRenderRowOption : int
{
    Dirty = 0,
}

internal enum GhosttyVtRenderCellData : int
{
    Invalid = 0,
    Raw = 1,
    Style = 2,
    GraphemesLength = 3,
    GraphemesBuffer = 4,
    BackgroundColor = 5,
    ForegroundColor = 6,
    Selected = 7,
    HasStyling = 8,
    GraphemesUtf8 = 9,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtRenderRowSelection
{
    internal nuint Size;
    internal ushort StartX;
    internal ushort EndX;

    internal static GhosttyVtRenderRowSelection CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtRenderRowSelection>()),
    };
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GhosttyVtRenderStateColors
{
    internal nuint Size;
    internal GhosttyVtColorRgb Background;
    internal GhosttyVtColorRgb Foreground;
    internal GhosttyVtColorRgb Cursor;
    internal byte CursorHasValue;
    internal fixed byte Palette[256 * 3];

    internal static GhosttyVtRenderStateColors CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtRenderStateColors>()),
    };
}

internal enum GhosttyVtCellContentTag : int
{
    Codepoint = 0,
    Grapheme = 1,
    BackgroundPalette = 2,
    BackgroundRgb = 3,
}

internal enum GhosttyVtCellWide : int
{
    Narrow = 0,
    Wide = 1,
    SpacerTail = 2,
    SpacerHead = 3,
}

internal enum GhosttyVtCellSemanticContent : int
{
    Output = 0,
    Input = 1,
    Prompt = 2,
}

internal enum GhosttyVtCellData : int
{
    Invalid = 0,
    Codepoint = 1,
    ContentTag = 2,
    Wide = 3,
    HasText = 4,
    HasStyling = 5,
    StyleId = 6,
    HasHyperlink = 7,
    Protected = 8,
    SemanticContent = 9,
    ColorPalette = 10,
    ColorRgb = 11,
}

internal enum GhosttyVtRowSemanticPrompt : int
{
    None = 0,
    Prompt = 1,
    PromptContinuation = 2,
}

internal enum GhosttyVtRowData : int
{
    Invalid = 0,
    Wrap = 1,
    WrapContinuation = 2,
    Grapheme = 3,
    Styled = 4,
    Hyperlink = 5,
    SemanticPrompt = 6,
    KittyVirtualPlaceholder = 7,
    Dirty = 8,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtPointCoordinate
{
    internal ushort X;
    internal uint Y;
}

internal enum GhosttyVtPointTag : int
{
    Active = 0,
    Viewport = 1,
    Screen = 2,
    History = 3,
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct GhosttyVtPointValue
{
    [FieldOffset(0)] internal GhosttyVtPointCoordinate Coordinate;
    [FieldOffset(0)] private readonly ulong _padding0;
    [FieldOffset(8)] private readonly ulong _padding1;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct GhosttyVtPoint
{
    [FieldOffset(0)] internal GhosttyVtPointTag Tag;
    [FieldOffset(8)] internal GhosttyVtPointValue Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtGridRef
{
    internal nuint Size;
    internal nint Node;
    internal ushort X;
    internal ushort Y;

    internal static GhosttyVtGridRef CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtGridRef>()),
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtSelection
{
    internal nuint Size;
    internal GhosttyVtGridRef Start;
    internal GhosttyVtGridRef End;
    internal byte Rectangle;

    internal static GhosttyVtSelection CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtSelection>()),
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtTerminalSearchOptions
{
    internal nuint Size;
    internal GhosttyVtString Query;
    internal long RequestedMatchIndex;
    internal nuint MaximumMatches;

    internal static GhosttyVtTerminalSearchOptions CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtTerminalSearchOptions>()),
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtTerminalSearchResult
{
    internal nuint Size;
    internal nuint MatchCount;
    internal nuint SelectedMatchIndex;
    internal byte ScanTruncated;

    internal static GhosttyVtTerminalSearchResult CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtTerminalSearchResult>()),
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtSelectWordOptions
{
    internal nuint Size;
    internal GhosttyVtGridRef Reference;
    internal nint BoundaryCodepoints;
    internal nuint BoundaryCodepointsLength;

    internal static GhosttyVtSelectWordOptions CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtSelectWordOptions>()),
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtSelectWordBetweenOptions
{
    internal nuint Size;
    internal GhosttyVtGridRef Start;
    internal GhosttyVtGridRef End;
    internal nint BoundaryCodepoints;
    internal nuint BoundaryCodepointsLength;

    internal static GhosttyVtSelectWordBetweenOptions CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtSelectWordBetweenOptions>()),
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtSelectLineOptions
{
    internal nuint Size;
    internal GhosttyVtGridRef Reference;
    internal nint WhitespaceCodepoints;
    internal nuint WhitespaceCodepointsLength;
    internal byte SemanticPromptBoundary;

    internal static GhosttyVtSelectLineOptions CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtSelectLineOptions>()),
    };
}

internal enum GhosttyVtFormatterFormat : int
{
    Plain = 0,
    Vt = 1,
    Html = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtSelectionFormatOptions
{
    internal nuint Size;
    internal GhosttyVtFormatterFormat Format;
    internal byte Unwrap;
    internal byte Trim;
    internal nint Selection;

    internal static GhosttyVtSelectionFormatOptions CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtSelectionFormatOptions>()),
    };
}

internal enum GhosttyVtSelectionOrder : int
{
    Forward = 0,
    Reverse = 1,
    MirroredForward = 2,
    MirroredReverse = 3,
}

internal enum GhosttyVtSelectionAdjust : int
{
    Left = 0,
    Right = 1,
    Up = 2,
    Down = 3,
    Home = 4,
    End = 5,
    PageUp = 6,
    PageDown = 7,
    BeginningOfLine = 8,
    EndOfLine = 9,
}

internal enum GhosttyVtKeyAction : int
{
    Release = 0,
    Press = 1,
    Repeat = 2,
}

[Flags]
internal enum GhosttyVtModifiers : ushort
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alt = 1 << 2,
    Super = 1 << 3,
    CapsLock = 1 << 4,
    NumLock = 1 << 5,
    RightShift = 1 << 6,
    RightControl = 1 << 7,
    RightAlt = 1 << 8,
    RightSuper = 1 << 9,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct GhosttyVtKey
{
    internal GhosttyVtKey(int value) => Value = value;

    internal readonly int Value;

    public static explicit operator GhosttyVtKey(int value) => new(value);
    public static implicit operator int(GhosttyVtKey value) => value.Value;
}

[Flags]
internal enum GhosttyVtKittyKeyFlags : byte
{
    Disabled = 0,
    Disambiguate = 1 << 0,
    ReportEvents = 1 << 1,
    ReportAlternates = 1 << 2,
    ReportAll = 1 << 3,
    ReportAssociated = 1 << 4,
    All = Disambiguate | ReportEvents | ReportAlternates | ReportAll | ReportAssociated,
}

internal enum GhosttyVtOptionAsAlt : int
{
    Disabled = 0,
    Enabled = 1,
    Left = 2,
    Right = 3,
}

internal enum GhosttyVtKeyEncoderOption : int
{
    CursorKeyApplication = 0,
    KeypadKeyApplication = 1,
    IgnoreKeypadWithNumLock = 2,
    AltEscapePrefix = 3,
    ModifyOtherKeysState2 = 4,
    KittyFlags = 5,
    MacOsOptionAsAlt = 6,
    BackarrowKeyMode = 7,
}

internal enum GhosttyVtMouseAction : int
{
    Press = 0,
    Release = 1,
    Motion = 2,
}

internal enum GhosttyVtMouseButton : int
{
    Unknown = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    Four = 4,
    Five = 5,
    Six = 6,
    Seven = 7,
    Eight = 8,
    Nine = 9,
    Ten = 10,
    Eleven = 11,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtMousePosition
{
    internal float X;
    internal float Y;
}

internal enum GhosttyVtMouseTrackingMode : int
{
    None = 0,
    X10 = 1,
    Normal = 2,
    Button = 3,
    Any = 4,
}

internal enum GhosttyVtMouseFormat : int
{
    X10 = 0,
    Utf8 = 1,
    Sgr = 2,
    Urxvt = 3,
    SgrPixels = 4,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtMouseEncoderSize
{
    internal nuint Size;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint CellWidth;
    internal uint CellHeight;
    internal uint PaddingTop;
    internal uint PaddingBottom;
    internal uint PaddingRight;
    internal uint PaddingLeft;

    internal static GhosttyVtMouseEncoderSize CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtMouseEncoderSize>()),
    };
}

internal enum GhosttyVtMouseEncoderOption : int
{
    Event = 0,
    Format = 1,
    Size = 2,
    AnyButtonPressed = 3,
    TrackLastCell = 4,
}

internal enum GhosttyVtFocusEvent : int
{
    Gained = 0,
    Lost = 1,
}

internal enum GhosttyVtKittyGraphicsData : int
{
    Invalid = 0,
    PlacementIterator = 1,
    Generation = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtKittyVirtualPlacementRenderInfo
{
    internal nuint Size;
    internal uint ImageId;
    internal uint PlacementId;
    internal int ViewportColumn;
    internal int ViewportRow;
    internal int ZIndex;
    internal uint CellOffsetX;
    internal uint CellOffsetY;
    internal uint SourceX;
    internal uint SourceY;
    internal uint SourceWidth;
    internal uint SourceHeight;
    internal uint DestinationWidth;
    internal uint DestinationHeight;

    internal static GhosttyVtKittyVirtualPlacementRenderInfo CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtKittyVirtualPlacementRenderInfo>()),
    };
}

internal enum GhosttyVtKittyPlacementData : int
{
    Invalid = 0,
    ImageId = 1,
    PlacementId = 2,
    IsVirtual = 3,
    XOffset = 4,
    YOffset = 5,
    SourceX = 6,
    SourceY = 7,
    SourceWidth = 8,
    SourceHeight = 9,
    Columns = 10,
    Rows = 11,
    Z = 12,
}

internal enum GhosttyVtKittyPlacementLayer : int
{
    All = 0,
    BelowBackground = 1,
    BelowText = 2,
    AboveText = 3,
}

internal enum GhosttyVtKittyPlacementIteratorOption : int
{
    Layer = 0,
}

internal enum GhosttyVtKittyImageFormat : int
{
    Rgb = 0,
    Rgba = 1,
    Png = 2,
    GrayAlpha = 3,
    Gray = 4,
}

internal enum GhosttyVtKittyImageCompression : int
{
    None = 0,
    ZlibDeflate = 1,
}

internal enum GhosttyVtKittyImageData : int
{
    Invalid = 0,
    Id = 1,
    Number = 2,
    Width = 3,
    Height = 4,
    Format = 5,
    Compression = 6,
    DataPointer = 7,
    DataLength = 8,
    Generation = 9,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyVtKittyPlacementRenderInfo
{
    internal nuint Size;
    internal uint PixelWidth;
    internal uint PixelHeight;
    internal uint GridColumns;
    internal uint GridRows;
    internal int ViewportColumn;
    internal int ViewportRow;
    internal byte ViewportVisible;
    internal uint SourceX;
    internal uint SourceY;
    internal uint SourceWidth;
    internal uint SourceHeight;

    internal static GhosttyVtKittyPlacementRenderInfo CreateSized() => new()
    {
        Size = checked((nuint)Marshal.SizeOf<GhosttyVtKittyPlacementRenderInfo>()),
    };
}
