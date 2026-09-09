using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Asura.App.Controls;

/// <summary>
/// Where the shell's remaining operating-system views live.
///
/// Native controls cannot participate in Avalonia composition and may not
/// survive destructive visual-tree or window changes. They stay parented here
/// for the lifetime of their owning panel; a panel only changes their geometry
/// and visibility. Browser content no longer uses this layer because its
/// off-screen renderer is an ordinary Avalonia visual.
/// </summary>
internal sealed class NativeSurfaceLayer : Canvas
{
    /// <summary>
    /// Every layer alive, so a suspension reaches all of them. Panels can be
    /// floated into windows of their own, and a drag that started in one window
    /// has to be visible in every window it might land in.
    /// </summary>
    private static readonly List<NativeSurfaceLayer> Layers = [];

    private static int _suspensions;

    private readonly Dictionary<Control, bool> _wanted = [];

    public NativeSurfaceLayer()
    {
        // The layer covers the window but is not a surface of its own: it is
        // hit-testable exactly where a native view sits and nowhere else, so the
        // interface underneath keeps working around them.
        Background = null;
        ClipToBounds = true;
        // Registered from birth, not from being shown. A layer that joined only
        // once it was attached would miss a suspension raised before it got
        // there, and come up with its surfaces on top of whatever asked for the
        // screen.
        Layers.Add(this);
    }

    /// <summary>
    /// Takes every native surface off the screen until the returned handle is
    /// disposed.
    ///
    /// There is no z-order to appeal to here. Avalonia draws its whole scene into
    /// one surface and the operating system composites native views above it, so
    /// native content steps aside while the shell needs its own drag targets or
    /// overlays to be visible.
    ///
    /// This costs nothing now that a surface outlives being hidden: no reload, no
    /// re-attach, no navigation. The page is exactly where it was.
    /// </summary>
    public static IDisposable Suspend()
    {
        if (Interlocked.Increment(ref _suspensions) == 1)
        {
            ApplyAll();
        }

        return new Suspension();
    }

    /// <summary>
    /// Raised when surfaces step aside and when they come back, so a panel can
    /// show what it is in the gap.
    /// </summary>
    public static event EventHandler? SuspensionChanged;

    public static bool IsSuspended => Volatile.Read(ref _suspensions) > 0;

    private static void ApplyAll()
    {
        SuspensionChanged?.Invoke(null, EventArgs.Empty);
        foreach (var layer in Layers.ToArray())
        {
            layer.ApplyAllHere();
        }
    }

    private sealed class Suspension : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (Interlocked.Decrement(ref _suspensions) == 0)
            {
                ApplyAll();
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!Layers.Contains(this))
        {
            Layers.Add(this);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Layers.Remove(this);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The layer for a control's window, or null until that window has one.
    ///
    /// The shell's own window declares one in markup. A floated panel's window is
    /// Dock's, and its content is the dockable itself — a view model drawn
    /// through a template — so there is nothing there to declare a layer beside
    /// and nothing to wrap: replacing that content puts a panel where a dockable
    /// was expected, and the window draws the words "Avalonia.Controls.Panel".
    /// The overlay every top level keeps for what is drawn over its content is
    /// where a native surface belongs.
    /// </summary>
    public static NativeSurfaceLayer? For(Visual visual)
    {
        if (visual.FindAncestorOfType<Window>() is not { } window)
        {
            return null;
        }

        if (window.GetVisualDescendants().OfType<NativeSurfaceLayer>().FirstOrDefault()
            is { } declared)
        {
            return declared;
        }

        if (OverlayLayer.GetOverlayLayer(window) is not { } overlay)
        {
            return null;
        }

        var layer = new NativeSurfaceLayer { ClipToBounds = false };
        overlay.Children.Add(layer);
        return layer;
    }

    /// <summary>
    /// Shows a surface at the given place in this layer, adopting it if this is
    /// the first time it has been seen.
    /// </summary>
    public void Present(Control surface, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!Children.Contains(surface))
        {
            // A panel floated into a window of its own, or came back from one.
            // Its surface goes with it — and it can only be in one place, so the
            // layer it was in has to let go before this one can take it. Adding a
            // control that still has a parent throws, which is why a floated
            // browser showed an empty panel: the page was still parented to the
            // window it had left.
            Surrender(surface);
            Children.Add(surface);
        }

        // A degenerate rect is what an unmeasured viewport reports, and moving a
        // native view to zero size is how some hosts decide it is gone.
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            _wanted[surface] = false;
            Apply(surface);
            return;
        }

        SetLeft(surface, bounds.X);
        SetTop(surface, bounds.Y);
        surface.Width = bounds.Width;
        surface.Height = bounds.Height;
        _wanted[surface] = true;
        Apply(surface);
    }

    /// <summary>
    /// Takes a surface out of whichever layer currently holds it, so another can
    /// take it. Not the same as releasing it: the panel still owns it, and the
    /// layer it is moving to is about to show it.
    ///
    /// Asked of the surface rather than of the layers, because by the time a
    /// panel comes back the window it was in has usually closed, and a layer
    /// stops being one of the layers the moment its window goes — while the
    /// surface is still parented to it. Looking for it among the living found
    /// nothing, and adding a control that still has a parent throws.
    /// </summary>
    private static void Surrender(Control surface)
    {
        if (surface.GetVisualParent() is not Panel previous)
        {
            return;
        }

        if (previous is NativeSurfaceLayer layer)
        {
            layer._wanted.Remove(surface);
        }

        previous.Children.Remove(surface);
    }

    /// <summary>
    /// Stops showing a surface without giving it up. The panel it belongs to is
    /// off screen — another tab is in front, or its view is being rebuilt — and
    /// will ask for it again.
    /// </summary>
    public void Conceal(Control surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (Children.Contains(surface))
        {
            _wanted[surface] = false;
            Apply(surface);
        }
    }

    /// <summary>
    /// Gives a surface up, for good. Only the owner of the panel calls this, when
    /// the panel itself is gone.
    /// </summary>
    public void Release(Control surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _wanted.Remove(surface);
        Children.Remove(surface);
    }

    /// <summary>
    /// A surface is on screen when its panel wants it there and nothing has
    /// asked the whole layer to stand down.
    /// </summary>
    private void Apply(Control surface) =>
        surface.IsVisible =
            _wanted.GetValueOrDefault(surface) && Volatile.Read(ref _suspensions) == 0;

    private void ApplyAllHere()
    {
        foreach (var surface in Children.ToArray())
        {
            Apply(surface);
        }
    }
}
