using Asura.App.Controls;
using Asura.Core;
using Avalonia.Media;

namespace Asura.App.ViewModels;

public sealed record TerminalProfileEditorSaveRequest(
    TerminalProfile Profile,
    long ExpectedRevision);

/// <summary>
/// A palette preset as the settings page shows it: the name, a few colours for
/// the tile's preview, and the palette itself to apply on selection.
/// </summary>
public sealed class TerminalPaletteOption : ObservableObject
{
    private bool _isSelected;

    public TerminalPaletteOption(TerminalPalette palette) =>
        Palette = palette ?? throw new ArgumentNullException(nameof(palette));

    public TerminalPalette Palette { get; }

    public string Name => Palette.Name;

    public string Background => Palette.Background.ToString();

    public string Foreground => Palette.Foreground.ToString();

    public string Green => Palette.AnsiColors[2].ToString();

    public string Blue => Palette.AnsiColors[4].ToString();

    /// <summary>
    /// Owned by the option so each tile can bind its own checked state; the
    /// editor sets it whenever the palette changes, including when the colour
    /// fields are edited back onto or away from a preset.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public override string ToString() => Name;
}

public sealed record TerminalKeymapOption(
    KeymapProfileId Id,
    string Name,
    bool IsAvailable)
{
    public string Label => IsAvailable ? Name : $"{Name} (missing)";

    public override string ToString() => Label;
}

public sealed class TerminalProfileEditorViewModel : ObservableObject
{
    private readonly TerminalProfile _original;
    private string _fontFamily;
    private double _fontSize;
    private double _lineHeight;
    private int _scrollbackLines;
    private TerminalCursorStyle _cursorStyle;
    private bool _cursorBlink;
    private string _foreground;
    private string _background;
    private string _cursor;
    private string _selection;
    private TerminalClipboardAccess _clipboardRead;
    private TerminalClipboardAccess _clipboardWrite;
    private TerminalLinkPolicy _linkPolicy;
    private bool _imeEnabled;
    private TerminalShellIntegrationMode _shellIntegration;
    private TerminalBellMode _bellMode;
    private TerminalCompatibilityProfile _compatibility;
    private TerminalKeymapOption _selectedKeymap;
    private string _paletteName;
    private IReadOnlyList<RgbColor> _ansiColors;

    public TerminalProfileEditorViewModel(
        TerminalProfile profile,
        long expectedRevision,
        IEnumerable<KeymapProfile>? keymaps = null)
    {
        _original = profile ?? throw new ArgumentNullException(nameof(profile));
        ExpectedRevision = expectedRevision;
        TerminalKeymaps = CreateTerminalKeymapOptions(profile, keymaps);
        _selectedKeymap = TerminalKeymaps.Single(option => option.Id == profile.KeymapId);
        _fontFamily = profile.FontFamily;
        _fontSize = profile.FontSize;
        _lineHeight = profile.LineHeight;
        _scrollbackLines = profile.ScrollbackLines;
        _cursorStyle = profile.CursorStyle;
        _cursorBlink = profile.CursorBlink;
        _foreground = profile.Palette.Foreground.ToString();
        _background = profile.Palette.Background.ToString();
        _cursor = profile.Palette.Cursor.ToString();
        _selection = profile.Palette.SelectionBackground.ToString();
        _clipboardRead = profile.ClipboardPolicy.ReadAccess;
        _clipboardWrite = profile.ClipboardPolicy.WriteAccess;
        _linkPolicy = profile.LinkPolicy;
        _imeEnabled = profile.ImeEnabled;
        _shellIntegration = profile.ShellIntegration;
        _bellMode = profile.BellMode;
        _compatibility = profile.Compatibility;
        _paletteName = profile.Palette.Name;
        _ansiColors = profile.Palette.AnsiColors;
        FontFamilies = BuildFontFamilies(profile.FontFamily);
        PalettePresets = [.. TerminalPalette.Presets.Select(preset => new TerminalPaletteOption(preset))];
        RefreshPaletteSelection();
    }

    private const int NormalAnsiColorCount = 8;

    private static readonly string[] AnsiNames =
        ["Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White"];

    /// <summary>
    /// Fixed-pitch families available to the renderer, with the bundled family
    /// always present and the profile's current value retained for review even
    /// when it is unavailable on this host.
    /// </summary>
    public IReadOnlyList<string> FontFamilies { get; }

    private static IReadOnlyList<string> BuildFontFamilies(string current)
    {
        var installed = InstalledFontFamilies();
        installed.Add(AsuraTerminalFontCollection.FamilyName);
        if (!string.IsNullOrWhiteSpace(current))
        {
            installed.Add(current);
        }

        return [.. installed
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// The editor is also constructed where no UI platform is running — tests and
    /// tooling — and the font manager only exists once one is. Without a host to
    /// ask, the bundled and stored families are the only ones that can be offered.
    /// </summary>
    private static List<string> InstalledFontFamilies()
    {
        try
        {
            var fontManager = FontManager.Current;
            return [.. fontManager.SystemFonts
                .Where(family =>
                {
                    try
                    {
                        return fontManager.TryGetGlyphTypeface(
                                new Typeface(family),
                                out var glyphTypeface)
                            && glyphTypeface.Metrics.IsFixedPitch;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                })
                .Select(family => family.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Colour fields are stored as text, but a picker works in colours. These
    /// wrap the same values so both editors stay in step.
    /// </summary>
    public Color ForegroundColor
    {
        get => ToColor(Foreground);
        set => Foreground = ToHex(value);
    }

    public Color BackgroundColor
    {
        get => ToColor(Background);
        set => Background = ToHex(value);
    }

    public Color CursorColor
    {
        get => ToColor(Cursor);
        set => Cursor = ToHex(value);
    }

    public Color SelectionColor
    {
        get => ToColor(Selection);
        set => Selection = ToHex(value);
    }

    private static Color ToColor(string value) =>
        Color.TryParse(value, out var color) ? color : Colors.Transparent;

    /// <summary>
    /// The palette stores six-digit RGB, so the picker's alpha is dropped rather
    /// than written into a field that cannot represent it.
    /// </summary>
    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>The palettes the settings page offers as one-click presets.</summary>
    public IReadOnlyList<TerminalPaletteOption> PalettePresets { get; }

    /// <summary>
    /// The preset whose colours the editor currently holds, or <c>null</c> when
    /// the colours have been edited away from every preset.
    /// </summary>
    public TerminalPaletteOption? SelectedPalettePreset =>
        TryBuildCurrentPalette(out var current)
            ? PalettePresets.FirstOrDefault(option => option.Palette.Matches(current))
            : null;

    public string PaletteName => SelectedPalettePreset?.Name ?? "Custom";

    public string ContrastWarning
    {
        get
        {
            if (!TryBuildCurrentPalette(out var palette))
            {
                return "Enter valid six-digit RGB values before this palette can be saved.";
            }

            var foreground = AppearanceContrast.TerminalForeground(palette);
            var cursor = AppearanceContrast.TerminalCursor(palette);
            var selectionBackground =
                AppearanceContrast.TerminalSelectionBackground(palette);
            var selectionText = AppearanceContrast.TerminalSelectionText(palette);
            var failingAnsi = AppearanceContrast.TerminalAnsi(palette)
                .Count(result => !result.MeetsRequirement);
            var warnings = new List<string>();
            if (!foreground.MeetsRequirement)
            {
                warnings.Add(FormattableString.Invariant(
                    $"text is {foreground.Ratio:0.00}:1; 4.5:1 is recommended"));
            }

            if (!cursor.MeetsRequirement)
            {
                warnings.Add(FormattableString.Invariant(
                    $"cursor is {cursor.Ratio:0.00}:1; 3:1 is recommended"));
            }

            if (!selectionBackground.MeetsRequirement)
            {
                warnings.Add(FormattableString.Invariant(
                    $"selection edge is {selectionBackground.Ratio:0.00}:1; 3:1 is recommended"));
            }

            if (!selectionText.MeetsRequirement)
            {
                warnings.Add(FormattableString.Invariant(
                    $"selected text is {selectionText.Ratio:0.00}:1; 4.5:1 is recommended"));
            }

            if (failingAnsi > 0)
            {
                warnings.Add(FormattableString.Invariant(
                    $"{failingAnsi} ANSI colors are below 4.5:1 against the background"));
            }

            return warnings.Count == 0
                ? string.Empty
                : "Contrast warning: " + string.Join("; ", warnings) + ".";
        }
    }

    public bool HasContrastWarning => ContrastWarning.Length > 0;

    /// <summary>
    /// Replaces every colour in the editor, including the sixteen ANSI entries,
    /// so choosing a preset changes the whole palette rather than only the four
    /// fields the page happens to show.
    /// </summary>
    public void ApplyPalettePreset(TerminalPalette preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        _paletteName = preset.Name;
        _ansiColors = preset.AnsiColors;
        Foreground = preset.Foreground.ToString();
        Background = preset.Background.ToString();
        Cursor = preset.Cursor.ToString();
        Selection = preset.SelectionBackground.ToString();
        RaisePaletteChanged();
    }

    /// <summary>
    /// Colour fields are edited as free text. While any of them is unparseable
    /// the palette has no well-defined value, so no preset is reported as
    /// selected rather than guessing at the last good one.
    /// </summary>
    private bool TryBuildCurrentPalette(out TerminalPalette palette)
    {
        palette = _original.Palette;
        if (!RgbColor.TryParse(Foreground, out var foreground)
            || !RgbColor.TryParse(Background, out var background)
            || !RgbColor.TryParse(Cursor, out var cursor)
            || !RgbColor.TryParse(Selection, out var selection))
        {
            return false;
        }

        palette = new TerminalPalette(
            _paletteName,
            foreground,
            background,
            cursor,
            selection,
            _ansiColors);
        return true;
    }

    private void RaisePaletteChanged()
    {
        RefreshPaletteSelection();
        OnPropertyChanged(nameof(ForegroundColor));
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(CursorColor));
        OnPropertyChanged(nameof(SelectionColor));
        OnPropertyChanged(nameof(NormalAnsiColors));
        OnPropertyChanged(nameof(SelectedPalettePreset));
        OnPropertyChanged(nameof(PaletteName));
        OnPropertyChanged(nameof(ContrastWarning));
        OnPropertyChanged(nameof(HasContrastWarning));
    }

    private void RefreshPaletteSelection()
    {
        var hasPalette = TryBuildCurrentPalette(out var current);
        foreach (var option in PalettePresets)
        {
            option.IsSelected = hasPalette && option.Palette.Matches(current);
        }
    }

    public IReadOnlyList<AnsiSwatchViewModel> NormalAnsiColors => [.. _ansiColors
        .Take(NormalAnsiColorCount)
        .Select((color, index) => new AnsiSwatchViewModel(AnsiNames[index], color.ToString()))];

    /// <summary>
    /// The revision this editor was opened against, and the one it will save
    /// with. It moves forward on a successful save so the catalog refresh that
    /// follows recognises the editor as current.
    ///
    /// Leaving it stale made the refresh replace the editor after every save, and
    /// a fresh editor re-raises its own binding changes — which is what the
    /// appearance page's auto-commit reads as a new edit. The two of them ran a
    /// commit loop at about eight writes a second.
    /// </summary>
    public long ExpectedRevision { get; private set; }

    public void AcceptSavedRevision(long revision) => ExpectedRevision = revision;

    public TerminalProfileId ProfileId => _original.Id;

    public IReadOnlyList<TerminalCursorStyle> CursorStyles { get; } = Enum.GetValues<TerminalCursorStyle>();

    public IReadOnlyList<TerminalClipboardAccess> ClipboardAccessOptions { get; } = Enum.GetValues<TerminalClipboardAccess>();

    public IReadOnlyList<TerminalLinkPolicy> LinkPolicies { get; } = Enum.GetValues<TerminalLinkPolicy>();

    public IReadOnlyList<TerminalShellIntegrationMode> ShellIntegrationModes { get; } = Enum.GetValues<TerminalShellIntegrationMode>();

    public IReadOnlyList<TerminalBellMode> BellModes { get; } = Enum.GetValues<TerminalBellMode>();

    public IReadOnlyList<TerminalCompatibilityProfile> CompatibilityProfiles { get; } = Enum.GetValues<TerminalCompatibilityProfile>();

    public IReadOnlyList<TerminalKeymapOption> TerminalKeymaps { get; }

    public string FontFamily { get => _fontFamily; set => SetProperty(ref _fontFamily, value); }

    public double FontSize { get => _fontSize; set => SetProperty(ref _fontSize, value); }

    public double LineHeight { get => _lineHeight; set => SetProperty(ref _lineHeight, value); }

    public int ScrollbackLines { get => _scrollbackLines; set => SetProperty(ref _scrollbackLines, value); }

    public TerminalCursorStyle CursorStyle { get => _cursorStyle; set => SetProperty(ref _cursorStyle, value); }

    public bool CursorBlink { get => _cursorBlink; set => SetProperty(ref _cursorBlink, value); }

    public string Foreground
    {
        get => _foreground;
        set
        {
            if (SetProperty(ref _foreground, value))
            {
                RaisePaletteChanged();
            }
        }
    }

    public string Background
    {
        get => _background;
        set
        {
            if (SetProperty(ref _background, value))
            {
                RaisePaletteChanged();
            }
        }
    }

    public string Cursor
    {
        get => _cursor;
        set
        {
            if (SetProperty(ref _cursor, value))
            {
                RaisePaletteChanged();
            }
        }
    }

    public string Selection
    {
        get => _selection;
        set
        {
            if (SetProperty(ref _selection, value))
            {
                RaisePaletteChanged();
            }
        }
    }

    public TerminalClipboardAccess ClipboardRead { get => _clipboardRead; set => SetProperty(ref _clipboardRead, value); }

    public TerminalClipboardAccess ClipboardWrite { get => _clipboardWrite; set => SetProperty(ref _clipboardWrite, value); }

    public TerminalLinkPolicy LinkPolicy { get => _linkPolicy; set => SetProperty(ref _linkPolicy, value); }

    public bool ImeEnabled { get => _imeEnabled; set => SetProperty(ref _imeEnabled, value); }

    public TerminalShellIntegrationMode ShellIntegration { get => _shellIntegration; set => SetProperty(ref _shellIntegration, value); }

    public TerminalBellMode BellMode { get => _bellMode; set => SetProperty(ref _bellMode, value); }

    public TerminalCompatibilityProfile Compatibility { get => _compatibility; set => SetProperty(ref _compatibility, value); }

    public TerminalKeymapOption SelectedKeymap
    {
        get => _selectedKeymap;
        set => SetProperty(
            ref _selectedKeymap,
            value ?? throw new ArgumentNullException(nameof(value)));
    }

    public bool MatchesTerminalKeymaps(IEnumerable<KeymapProfile> keymaps)
    {
        ArgumentNullException.ThrowIfNull(keymaps);
        return TerminalKeymaps.SequenceEqual(CreateTerminalKeymapOptions(_original, keymaps));
    }

    public TerminalProfileEditorSaveRequest CreateSaveRequest()
    {
        var palette = new TerminalPalette(
            _paletteName,
            RgbColor.Parse(Foreground),
            RgbColor.Parse(Background),
            RgbColor.Parse(Cursor),
            RgbColor.Parse(Selection),
            _ansiColors);
        return new TerminalProfileEditorSaveRequest(
            new TerminalProfile(
                _original.Id,
                _original.Name,
                FontFamily,
                FontSize,
                LineHeight,
                CursorStyle,
                CursorBlink,
                ScrollbackLines,
                palette,
                SelectedKeymap.Id,
                new TerminalClipboardPolicy(ClipboardRead, ClipboardWrite, _original.ClipboardPolicy.PasteSafety),
                LinkPolicy,
                ImeEnabled,
                ShellIntegration,
                BellMode,
                Compatibility),
            ExpectedRevision);
    }

    private static IReadOnlyList<TerminalKeymapOption> CreateTerminalKeymapOptions(
        TerminalProfile profile,
        IEnumerable<KeymapProfile>? keymaps)
    {
        var available = (keymaps ?? BuiltInKeymaps.All)
            .Where(keymap => keymap.Layer == KeymapLayer.Terminal)
            .GroupBy(keymap => keymap.Id)
            .Select(group => group.First())
            .OrderBy(keymap => keymap.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(keymap => keymap.Id.Value, StringComparer.Ordinal)
            .Select(keymap => new TerminalKeymapOption(keymap.Id, keymap.Name, IsAvailable: true))
            .ToList();
        if (available.All(option => option.Id != profile.KeymapId))
        {
            available.Add(new TerminalKeymapOption(
                profile.KeymapId,
                profile.KeymapId.Value,
                IsAvailable: false));
        }

        return Array.AsReadOnly(available.ToArray());
    }
}
