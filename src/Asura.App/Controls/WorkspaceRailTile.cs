using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using FluentIcons.Common;

namespace Asura.App.Controls;

/// <summary>
/// One workspace in the shell's rail.
///
/// The rail has to answer three different questions at a glance — which
/// workspace am I in, which others are running, and which is asking for me —
/// and it used to answer them with one background colour and two identical grey
/// outlines. Every tile looked equally lit, so the only way to tell where you
/// were was to read the canvas.
///
/// So the tile says it in colour instead. The one you are in wears its own
/// colour whole; the others are strongly desaturated and come back to full
/// colour under the pointer, which makes hovering the rail a way of reading it.
/// A workspace that is running but not in front is outlined in its own accent,
/// so "alive" and "in front" are two different marks rather than two shades of
/// the same one. The colours are the workspace's own, computed here, because a
/// desaturated variant of an arbitrary accent is not something a style sheet
/// can express and not something a view model should be inventing.
/// </summary>
internal sealed class WorkspaceRailTile : TemplatedControl
{
    public static readonly StyledProperty<string?> AccentProperty =
        AvaloniaProperty.Register<WorkspaceRailTile, string?>(nameof(Accent));

    public static readonly StyledProperty<Symbol> IconProperty =
        AvaloniaProperty.Register<WorkspaceRailTile, Symbol>(nameof(Icon), Symbol.Window);

    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<WorkspaceRailTile, bool>(nameof(IsRunning));

    public static readonly StyledProperty<bool> IsCurrentProperty =
        AvaloniaProperty.Register<WorkspaceRailTile, bool>(nameof(IsCurrent));

    public static readonly StyledProperty<bool> IsIsolatedProperty =
        AvaloniaProperty.Register<WorkspaceRailTile, bool>(nameof(IsIsolated));

    public static readonly StyledProperty<bool> HasAttentionProperty =
        AvaloniaProperty.Register<WorkspaceRailTile, bool>(nameof(HasAttention));

    public static readonly StyledProperty<bool> HasAgentActivityProperty =
        AvaloniaProperty.Register<WorkspaceRailTile, bool>(nameof(HasAgentActivity));

    /// <summary>
    /// Whether this tile offers to end the workspace. False for the one that
    /// always exists — it has nowhere to go — and false for anything not
    /// running, which has nothing to end.
    /// </summary>
    public static readonly StyledProperty<bool> CanCloseProperty =
        AvaloniaProperty.Register<WorkspaceRailTile, bool>(nameof(CanClose));

    public static readonly StyledProperty<bool> CanSaveProperty =
        AvaloniaProperty.Register<WorkspaceRailTile, bool>(nameof(CanSave));

    public bool CanSave
    {
        get => GetValue(CanSaveProperty);
        set => SetValue(CanSaveProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? SaveRequested;

    /// <summary>
    /// Which way the tile grows when it opens its close action. It overflows the
    /// rail rather than widening it, so it has to grow away from the window
    /// edge the rail is docked against.
    /// </summary>
    public static readonly StyledProperty<bool> ExpandsLeftProperty =
        AvaloniaProperty.Register<WorkspaceRailTile, bool>(nameof(ExpandsLeft));

    public static readonly DirectProperty<WorkspaceRailTile, IBrush?> AccentBrushProperty =
        AvaloniaProperty.RegisterDirect<WorkspaceRailTile, IBrush?>(
            nameof(AccentBrush),
            tile => tile.AccentBrush);

    public static readonly DirectProperty<WorkspaceRailTile, IBrush?> RestingBrushProperty =
        AvaloniaProperty.RegisterDirect<WorkspaceRailTile, IBrush?>(
            nameof(RestingBrush),
            tile => tile.RestingBrush);

    public static readonly DirectProperty<WorkspaceRailTile, IBrush?> MarkBrushProperty =
        AvaloniaProperty.RegisterDirect<WorkspaceRailTile, IBrush?>(
            nameof(MarkBrush),
            tile => tile.MarkBrush);

    private IBrush? _accentBrush;
    private IBrush? _restingBrush;
    private IBrush? _markBrush;

    static WorkspaceRailTile()
    {
        AccentProperty.Changed.AddClassHandler<WorkspaceRailTile>(
            (tile, _) => tile.RefreshBrushes());
        IsRunningProperty.Changed.AddClassHandler<WorkspaceRailTile>(
            (tile, _) => tile.RefreshStateClasses());
        IsCurrentProperty.Changed.AddClassHandler<WorkspaceRailTile>(
            (tile, _) => tile.RefreshStateClasses());
        HasAttentionProperty.Changed.AddClassHandler<WorkspaceRailTile>(
            (tile, _) => tile.RefreshStateClasses());
        CanCloseProperty.Changed.AddClassHandler<WorkspaceRailTile>(
            (tile, _) => tile.RefreshStateClasses());
        CanSaveProperty.Changed.AddClassHandler<WorkspaceRailTile>(
            (tile, _) => tile.RefreshStateClasses());
        ExpandsLeftProperty.Changed.AddClassHandler<WorkspaceRailTile>(
            (tile, _) => tile.RefreshStateClasses());
    }

    public WorkspaceRailTile()
    {
        RefreshBrushes();
        RefreshStateClasses();
    }

    /// <summary>Asked for when the tile is clicked.</summary>
    public event EventHandler<RoutedEventArgs>? OpenRequested;

    /// <summary>Asked for when the tile's close action is clicked.</summary>
    public event EventHandler<RoutedEventArgs>? CloseRequested;

    /// <summary>The workspace's own colour, as the hex the definition stores.</summary>
    public string? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Symbol Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public bool IsCurrent
    {
        get => GetValue(IsCurrentProperty);
        set => SetValue(IsCurrentProperty, value);
    }

    public bool IsIsolated
    {
        get => GetValue(IsIsolatedProperty);
        set => SetValue(IsIsolatedProperty, value);
    }

    public bool HasAgentActivity
    {
        get => GetValue(HasAgentActivityProperty);
        set => SetValue(HasAgentActivityProperty, value);
    }

    public bool HasAttention
    {
        get => GetValue(HasAttentionProperty);
        set => SetValue(HasAttentionProperty, value);
    }

    public bool CanClose
    {
        get => GetValue(CanCloseProperty);
        set => SetValue(CanCloseProperty, value);
    }

    public bool ExpandsLeft
    {
        get => GetValue(ExpandsLeftProperty);
        set => SetValue(ExpandsLeftProperty, value);
    }

    /// <summary>The workspace's colour at full strength.</summary>
    public IBrush? AccentBrush
    {
        get => _accentBrush;
        private set => SetAndRaise(AccentBrushProperty, ref _accentBrush, value);
    }

    /// <summary>The same colour with most of the saturation taken out of it.</summary>
    public IBrush? RestingBrush
    {
        get => _restingBrush;
        private set => SetAndRaise(RestingBrushProperty, ref _restingBrush, value);
    }

    /// <summary>
    /// What the icon is drawn in. Measured against the workspace's own colour
    /// rather than taken from the shell's accent pair: the tile is filled with a
    /// colour the theme knows nothing about, so only the tile can say whether
    /// ink or paper reads on it.
    /// </summary>
    public IBrush? MarkBrush
    {
        get => _markBrush;
        private set => SetAndRaise(MarkBrushProperty, ref _markBrush, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_Open") is { } open)
        {
            open.Click += OnOpenClick;
        }

        if (e.NameScope.Find<Button>("PART_Close") is { } close)
        {
            close.Click += OnCloseClick;
        }
        if (e.NameScope.Find<Button>("PART_Save") is { } save)
        {
            save.Click += OnSaveClick;
        }
    }

    /// <summary>
    /// How much of the colour survives in a resting tile. Enough that a red
    /// workspace and a green one are still told apart in the rail, far too
    /// little to compete with the one you are actually in.
    /// </summary>
    private const double SurvivingSaturation = 0.22;

    /// <summary>How far a resting tile is dimmed toward the rail behind it.</summary>
    private const double RestingBrightness = 0.66;

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        OpenRequested?.Invoke(this, e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        CloseRequested?.Invoke(this, e);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        SaveRequested?.Invoke(this, e);
    }

    private void RefreshBrushes()
    {
        if (!Color.TryParse(Accent, out var accent))
        {
            AccentBrush = null;
            RestingBrush = null;
            MarkBrush = null;
            return;
        }

        AccentBrush = new SolidColorBrush(accent);
        RestingBrush = new SolidColorBrush(Rest(accent));
        // Measured against the full colour rather than the resting one: the
        // icon must not change colour when the tile lights up under the
        // pointer, and the full colour is the brighter of the two, so a mark
        // that reads on it reads on both.
        MarkBrush = new SolidColorBrush(Ink(accent));
    }

    /// <summary>
    /// Ink or paper, whichever stands out on a fill. The threshold is the
    /// perceptual midpoint rather than 128, which would put white text on
    /// mid-greens.
    /// </summary>
    private static Color Ink(Color fill) =>
        (0.2126 * fill.R) + (0.7152 * fill.G) + (0.0722 * fill.B) > 150
            ? Color.FromRgb(0x14, 0x14, 0x16)
            : Colors.White;

    /// <summary>
    /// Pulls a colour toward the grey of its own brightness, then dims it.
    ///
    /// Rec. 709 luma rather than a channel average, so a yellow tile and a blue
    /// one do not both come out as the same middle grey — the point of leaving
    /// any colour at all is that the rail stays readable at rest.
    /// </summary>
    private static Color Rest(Color accent)
    {
        var luma = 0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B;
        return Color.FromRgb(
            Muted(accent.R, luma),
            Muted(accent.G, luma),
            Muted(accent.B, luma));
    }

    private static byte Muted(byte channel, double luma) =>
        (byte)Math.Clamp(
            (luma + ((channel - luma) * SurvivingSaturation)) * RestingBrightness,
            0,
            255);

    private void RefreshStateClasses()
    {
        PseudoClasses.Set(":running", IsRunning);
        PseudoClasses.Set(":current", IsCurrent);
        PseudoClasses.Set(":attention", HasAttention);
        PseudoClasses.Set(":closable", CanClose);
        PseudoClasses.Set(":saveable", CanSave);
        PseudoClasses.Set(":expandsleft", ExpandsLeft);
    }
}
