using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Asura.App.Controls;

/// <summary>
/// Opts a control into having its corners worked out from the one it sits
/// inside.
///
/// Nested rounded rectangles only look like one surface when their curves
/// share a centre, which means the inner radius is the outer radius less the
/// distance between them. Apple states that rule and, from macOS 26, applies
/// it for you through <c>ConcentricRectangle</c> and <c>containerShape</c>.
/// Avalonia has no equivalent, so this is that rule rather than a port of
/// their implementation, which is not published.
///
/// An attached property rather than a border subclass, because Avalonia's type
/// selectors match one type exactly: a <c>Border.FloatingSidebar</c> style
/// stops applying the moment the element is a subclass of Border, and a
/// sidebar that quietly loses its background and margin does not look like a
/// styling rule — it looks like it vanished. Opting in by property leaves the
/// element the type its styles were written against.
///
/// A container is marked with <see cref="IsContainerProperty"/> — or is simply
/// the nearest ancestor that carries a radius. The distance is measured from
/// the arranged bounds rather than added up from margins and padding, so it
/// stays right whatever put the gap there.
///
/// The corners are still circular arcs. Apple's are continuous curvature —
/// squircles — which no <see cref="Border"/> can draw, and which would need
/// the fill, the stroke and the clip all replaced by geometry. The
/// relationships are what read as wrong when they are wrong; the curve is a
/// separate question, and a smaller one below about twelve points.
/// </summary>
public static class Concentric
{
    /// <summary>Whether this element works its corners out from its container.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsEnabled",
            typeof(Concentric));

    /// <summary>
    /// Marks an element as the shape inner corners are measured against.
    /// </summary>
    public static readonly AttachedProperty<bool> IsContainerProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>(
            "IsContainer",
            typeof(Concentric));

    /// <summary>
    /// The radius the container is drawn with, for a container that does not
    /// carry one itself — a window's frame, drawn by the platform.
    /// </summary>
    public static readonly AttachedProperty<double> ContainerRadiusProperty =
        AvaloniaProperty.RegisterAttached<Visual, double>(
            "ContainerRadius",
            typeof(Concentric));

    /// <summary>
    /// How tight a derived corner may become. Below this a corner reads as
    /// square, and squaring one corner of a rounded thing looks like a mistake
    /// rather than a decision.
    /// </summary>
    public static readonly AttachedProperty<double> MinimumRadiusProperty =
        AvaloniaProperty.RegisterAttached<Control, double>(
            "MinimumRadius",
            typeof(Concentric),
            defaultValue: 2);

    private static readonly AttachedProperty<ConcentricCornerReconciler?> ReconcilerProperty =
        AvaloniaProperty.RegisterAttached<Control, ConcentricCornerReconciler?>(
            "Reconciler",
            typeof(Concentric));

    static Concentric() =>
        IsEnabledProperty.Changed.AddClassHandler<Control>(OnIsEnabledChanged);

    public static bool GetIsEnabled(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(Control element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    public static bool GetIsContainer(Visual element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsContainerProperty);
    }

    public static void SetIsContainer(Visual element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsContainerProperty, value);
    }

    public static double GetContainerRadius(Visual element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ContainerRadiusProperty);
    }

    public static void SetContainerRadius(Visual element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ContainerRadiusProperty, value);
    }

    public static double GetMinimumRadius(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(MinimumRadiusProperty);
    }

    public static void SetMinimumRadius(Control element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(MinimumRadiusProperty, value);
    }

    private static void OnIsEnabledChanged(Control element, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.GetNewValue<bool>())
        {
            if (element.GetValue(ReconcilerProperty) is not null)
            {
                return;
            }

            var reconciler = new ConcentricCornerReconciler(
                element,
                GetMinimumRadius(element));
            element.SetValue(ReconcilerProperty, reconciler);
            element.LayoutUpdated += reconciler.OnLayoutUpdated;
            return;
        }

        if (element.GetValue(ReconcilerProperty) is not { } existing)
        {
            return;
        }

        element.LayoutUpdated -= existing.OnLayoutUpdated;
        existing.Detach();
        element.SetValue(ReconcilerProperty, null);
    }
}

/// <summary>
/// Keeps one control's corners answering to the surface it sits inside.
///
/// Owned by the control rather than applied to it, so the theme's own value
/// stays reachable: the rule needs it both as the answer for corners that are
/// not concentric and as the thing to fall back to when it stops applying at
/// all.
/// </summary>
internal sealed class ConcentricCornerReconciler
{
    private readonly Control _owner;
    private readonly Func<double> _minimumRadius;
    private bool _writing;
    private Visual? _watched;

    public ConcentricCornerReconciler(Control owner, double minimumRadius)
        : this(owner, () => minimumRadius)
    {
    }

    /// <summary>
    /// The floor can depend on what the element is and what it holds, and both
    /// answer to the appearance setting, so it is asked for at each reconcile
    /// rather than fixed when the rule was attached.
    /// </summary>
    public ConcentricCornerReconciler(Control owner, Func<double> minimumRadius)
    {
        _owner = owner;
        _minimumRadius = minimumRadius;
    }

    public void OnLayoutUpdated(object? sender, EventArgs args) => Reconcile();

    public void Detach() => Watch(null);

    /// <summary>
    /// Follows the surface this one measures itself against.
    ///
    /// Layout is not the only thing that moves a radius: changing the corner
    /// setting republishes the container's without moving a single element, so
    /// waiting for a layout pass meant answering the press before last — which
    /// looked like the setting needing to be pressed twice.
    ///
    /// The container's value is the one to watch rather than this element's
    /// own. Once a derived radius is written here it is a local value, and a
    /// local value silences the style changes underneath it.
    /// </summary>
    private void Watch(Visual? container)
    {
        if (ReferenceEquals(_watched, container))
        {
            return;
        }

        _watched?.PropertyChanged -= OnContainerPropertyChanged;

        _watched = container;
        _watched?.PropertyChanged += OnContainerPropertyChanged;
    }

    private void OnContainerPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Concentric.ContainerRadiusProperty
            || args.Property == Border.CornerRadiusProperty)
        {
            Reconcile();
        }
    }

    public void Reconcile()
    {
        // Clearing and setting both raise the change this listens for.
        if (_writing)
        {
            return;
        }

        _writing = true;
        try
        {
            // Back to the theme's value before reading it. The fallback has to
            // be a style, not whatever this worked out last time — a value
            // that feeds on itself stops tracking the setting entirely.
            _owner.ClearValue(Border.CornerRadiusProperty);
            var authored = _owner.GetValue(Border.CornerRadiusProperty);

            var (container, derived) = ConcentricCorners.DeriveFor(_owner, _minimumRadius());
            Watch(container);
            if (derived is { } radius && !radius.Equals(authored))
            {
                _owner.SetValue(Border.CornerRadiusProperty, radius);
            }
        }
        finally
        {
            _writing = false;
        }
    }
}

/// <summary>
/// The rule itself, apart from any control: an inner radius is its container's
/// less the distance between them, so the two curves share a centre.
/// </summary>
public static class ConcentricCorners
{
    /// <summary>
    /// Works the radius out for a control from the nearest rounded surface it
    /// sits inside. Null when there is no such surface, or when the rule does
    /// not apply to where the control sits in it.
    /// </summary>
    public static (Visual? Container, CornerRadius? Radius) DeriveFor(
        Control element,
        double minimumRadius)
    {
        ArgumentNullException.ThrowIfNull(element);
        var (container, outer) = FindContainer(element);
        if (container is null
            || element.TranslatePoint(default, container) is not { } offset)
        {
            return (container, null);
        }

        return (container, Derive(
            outer,
            container.Bounds.Size,
            offset,
            element.Bounds.Size,
            minimumRadius));
    }

    private static (Visual? Container, double Radius) FindContainer(Control element)
    {
        foreach (var ancestor in element.GetVisualAncestors())
        {
            // A scroll boundary ends the search. Past it the distance to
            // anything outside is whatever the scroll position happens to be,
            // so a radius derived from it would change as the content moved —
            // corners quietly growing and shrinking while you scroll. A
            // container found before the boundary is still good: it scrolls
            // with the element, so the gap between them holds.
            if (StopsTheSearch(ancestor))
            {
                return (null, 0);
            }

            if (Concentric.GetIsContainer(ancestor))
            {
                return (ancestor, LargestCorner(ancestor));
            }

            if (ancestor is Border { CornerRadius: var corner } && corner != default)
            {
                return (ancestor, LargestCorner(ancestor));
            }

            if (ancestor is TemplatedControl { CornerRadius: var themed } && themed != default)
            {
                return (ancestor, LargestCorner(ancestor));
            }
        }

        return (null, 0);
    }

    /// <summary>
    /// Whether an ancestor ends the search for something to be concentric
    /// with. A scroll boundary does: past it the distance to anything outside
    /// is whatever the scroll position happens to be, so a radius taken from
    /// it would grow and shrink as the content moved.
    /// </summary>
    internal static bool StopsTheSearch(Visual ancestor) =>
        ancestor is ScrollViewer or ScrollContentPresenter;

    private static double LargestCorner(Visual element)
    {
        var declared = Concentric.GetContainerRadius(element);
        if (declared > 0)
        {
            return declared;
        }

        var corner = element switch
        {
            Border border => border.CornerRadius,
            TemplatedControl templated => templated.CornerRadius,
            _ => default,
        };

        return Math.Max(
            Math.Max(corner.TopLeft, corner.TopRight),
            Math.Max(corner.BottomLeft, corner.BottomRight));
    }

    /// <summary>
    /// Returns null when there is nothing to derive from — an unarranged
    /// element, or a container with square corners, where guessing would be
    /// worse than leaving the radius alone.
    /// </summary>
    public static CornerRadius? Derive(
        double outerRadius,
        Size containerSize,
        Point offsetInContainer,
        Size size,
        double minimumRadius)
    {
        if (outerRadius <= 0
            || size.Width <= 0
            || size.Height <= 0
            || containerSize.Width <= 0
            || containerSize.Height <= 0)
        {
            return null;
        }

        // The shortest way to the container, whichever side that is.
        //
        // Corner by corner reads wrong on anything tall or wide: a sidebar
        // below a tab strip is far from the window's top corners and hard
        // against its bottom ones, and giving it two tight corners and two
        // round ones makes one shape look like two. It is one surface sitting
        // one distance inside another, so it takes one radius — the one that
        // its closest edge earns.
        var gap = Math.Min(
            Math.Min(offsetInContainer.X, offsetInContainer.Y),
            Math.Min(
                containerSize.Width - (offsetInContainer.X + size.Width),
                containerSize.Height - (offsetInContainer.Y + size.Height)));

        // Something hanging outside the container is not inside its shape, so
        // there is no shared curve to answer to. Treating the overflow as flush
        // — which clamping the distance to zero did — handed it the container's
        // whole radius, so a card whose content outgrew the panel came out
        // rounder than the cards beside it. That is where a row of identical
        // cards stopped agreeing.
        if (gap < 0)
        {
            return null;
        }

        // Further out than the radius itself is not near the container's
        // corners at all, and stepping in by that distance would square
        // something meant to be round. There it keeps what it was given.
        //
        // The same is true just short of that distance: a card eight points
        // inside a nine-point panel earns a one-point corner, which is square
        // beside the seven-point controls it holds — the inner curve reading
        // tighter than everything within it. The rule applies where it leaves
        // a corner worth drawing, and stands aside where it does not, rather
        // than clamping to a floor nothing else in the interface uses.
        var derived = outerRadius - gap;
        return derived < minimumRadius
            ? null
            : new CornerRadius(derived);
    }
}
