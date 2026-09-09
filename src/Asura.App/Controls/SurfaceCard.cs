using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Asura.App.Controls;

/// <summary>
/// How a card sits against the surface behind it.
/// </summary>
internal enum SurfaceTone
{
    /// <summary>The default card: content grouped on the page's own surface.</summary>
    Default,

    /// <summary>A card that sits above its parent — a control inside another card.</summary>
    Raised,

    /// <summary>A well: a region the page recedes into, such as a panel canvas.</summary>
    Sunken,

    /// <summary>
    /// A floating panel region on the window canvas — the surface the workspaces
    /// sidebar uses. Cards sit on it with their own fills.
    /// </summary>
    Panel,

    /// <summary>Something worth reading before acting on the rest of the page.</summary>
    Notice,

    /// <summary>Something that has gone wrong, or that cannot be undone.</summary>
    Danger,

    /// <summary>A bounded permission, or an action that will ask before it proceeds.</summary>
    Warning,

    /// <summary>Something that completed, or a state that is healthy.</summary>
    Success,
}

/// <summary>
/// How far a card is lifted off the page.
/// </summary>
internal enum SurfaceElevation
{
    /// <summary>Flush with the page.</summary>
    Flat,

    /// <summary>Floating over it — a popup, a palette, a dialog body.</summary>
    Overlay,
}

/// <summary>
/// The shell's one card.
///
/// There used to be eight of them — settings, notice, control, empty-state, panel,
/// launch, list, and overlay — plus ninety-six bordered <c>Border</c> elements that
/// carried their own fill, radius, and inset inline, in thirty-five distinct
/// shapes. They differed in exactly two things: which surface they sat on, and
/// whether they cast a shadow. Everything else about them was the same intent
/// written down again, slightly differently each time, which is why a change to
/// what a card looks like meant finding every one of them.
///
/// This is that intent, once, with the two differences as configuration. A view
/// says what a card <em>is</em> — <c>Tone="Notice"</c> — and never what it looks
/// like, so the answer can change in one place and so it can differ per platform
/// without any view knowing.
/// </summary>
internal sealed class SurfaceCard : ContentControl
{
    public static readonly StyledProperty<SurfaceTone> ToneProperty =
        AvaloniaProperty.Register<SurfaceCard, SurfaceTone>(nameof(Tone));

    public static readonly StyledProperty<SurfaceElevation> ElevationProperty =
        AvaloniaProperty.Register<SurfaceCard, SurfaceElevation>(nameof(Elevation));

    /// <summary>
    /// Whether the card clips what it contains to its own corners.
    ///
    /// Off by default because clipping costs a layer, and most cards hold nothing
    /// that reaches their edge. A card whose content is a filled header or a
    /// terminal has to turn it on, or the content squares off the corners the
    /// border draws round.
    /// </summary>
    public static readonly StyledProperty<bool> ClipsContentProperty =
        AvaloniaProperty.Register<SurfaceCard, bool>(nameof(ClipsContent));

    /// <summary>
    /// Whether the card reacts to the pointer and reads as one target.
    /// </summary>
    public static readonly StyledProperty<bool> IsInteractiveProperty =
        AvaloniaProperty.Register<SurfaceCard, bool>(nameof(IsInteractive));

    static SurfaceCard()
    {
        // The tone and elevation are pseudo-classes rather than styled setters so
        // one selector can state a whole appearance, and so a host profile can
        // restate any of them without this control knowing the profile exists.
        ToneProperty.Changed.AddClassHandler<SurfaceCard>(
            (card, _) => card.UpdateStateClasses());
        ElevationProperty.Changed.AddClassHandler<SurfaceCard>(
            (card, _) => card.UpdateStateClasses());
        // Deliberately not set on the card itself: the template's outer border
        // draws the outline, and a control that clips to its own bounds clips that
        // stroke away at the corners. The inner border does the clipping.
        IsInteractiveProperty.Changed.AddClassHandler<SurfaceCard>(
            (card, _) => card.UpdateStateClasses());
    }

    /// <summary>
    /// A card inside a rounded surface takes its corners from that surface,
    /// less the gap to it, so the two curves share a centre instead of merely
    /// both being round. Where it does not sit inside one — or sits nowhere
    /// near its corners — the theme's own radius stands.
    /// </summary>
    private readonly ConcentricCornerReconciler _corners;

    public SurfaceCard()
    {
        _corners = new ConcentricCornerReconciler(this, MinimumConcentricRadius);
        LayoutUpdated += (_, _) => _corners.Reconcile();
        UpdateStateClasses();
    }

    /// <summary>
    /// How tight a derived corner may become before the rule stands aside.
    ///
    /// A card holds controls, and a card cannot be tighter than what it holds:
    /// an input rounder than the card around it is what reads as broken, and it
    /// is what a card eight points inside a panel earned — five, against the
    /// ten its own inputs were drawn with. So the floor is the control corner
    /// rather than a hairline, and it follows the appearance setting the way
    /// the controls do.
    /// </summary>
    private double MinimumConcentricRadius() =>
        this.TryFindResource("ShellControlCornerRadius", out var value)
            && value is CornerRadius radius
            ? Math.Max(
                Math.Max(radius.TopLeft, radius.TopRight),
                Math.Max(radius.BottomLeft, radius.BottomRight))
            : HairlineMinimumRadius;

    /// <summary>The floor where the control corner cannot be resolved.</summary>
    private const double HairlineMinimumRadius = 2;

    public SurfaceTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public SurfaceElevation Elevation
    {
        get => GetValue(ElevationProperty);
        set => SetValue(ElevationProperty, value);
    }

    public bool ClipsContent
    {
        get => GetValue(ClipsContentProperty);
        set => SetValue(ClipsContentProperty, value);
    }

    public bool IsInteractive
    {
        get => GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SurfaceCard);

    private void UpdateStateClasses()
    {
        foreach (var tone in Enum.GetValues<SurfaceTone>())
        {
            PseudoClasses.Set($":tone-{ToneName(tone)}", tone == Tone);
        }

        PseudoClasses.Set(":overlay", Elevation == SurfaceElevation.Overlay);
        PseudoClasses.Set(":interactive", IsInteractive);
    }

    private static string ToneName(SurfaceTone tone) => tone switch
    {
        SurfaceTone.Default => "default",
        SurfaceTone.Raised => "raised",
        SurfaceTone.Sunken => "sunken",
        SurfaceTone.Panel => "panel",
        SurfaceTone.Notice => "notice",
        SurfaceTone.Danger => "danger",
        SurfaceTone.Warning => "warning",
        SurfaceTone.Success => "success",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, "Unknown surface tone."),
    };
}
