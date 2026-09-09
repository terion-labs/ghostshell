using Avalonia;
using Dock.Model.Controls;

namespace Asura.App.ViewModels;

/// <summary>
/// A panel the tiled layout has let go of, floating over the workspace it
/// belongs to.
///
/// It floats <em>inside</em> the shell's window rather than in a window of its
/// own. Keeping the document itself intact means a browser visual can be
/// reparented without rebuilding its renderer or session, while any remaining
/// operating-system views avoid a destructive cross-window move. Floating is a
/// question of where the panel is drawn, not how its content is rebuilt.
///
/// The document travels with it, unattached, so the panel keeps its identity and
/// the layout can put it back exactly where it was.
/// </summary>
public sealed class FloatingRuntimePanelViewModel : ObservableObject
{
    /// <summary>
    /// Enough of the workspace to be usable, little enough to still see what is
    /// underneath. Successive panels step down and across so a second one does
    /// not land exactly on the first.
    /// </summary>
    private const double DefaultWidth = 760;
    private const double DefaultHeight = 520;
    private const double CascadeStep = 28;

    /// <summary>
    /// How much of a panel stays inside the workspace however far it is dragged.
    /// A panel pushed past the edge and released is a panel with no header left
    /// to take hold of.
    /// </summary>
    private const double Reachable = 80;

    private double _x;
    private double _y;
    private double _width = DefaultWidth;
    private double _height = DefaultHeight;

    public FloatingRuntimePanelViewModel(
        RuntimePanelViewModel panel,
        IDocument document,
        int cascade)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(document);
        Panel = panel;
        Document = document;
        _x = CascadeStep * (cascade + 1);
        _y = CascadeStep * (cascade + 1);
    }

    public RuntimePanelViewModel Panel { get; }

    /// <summary>
    /// The panel's place in the dock graph, kept while it has none. Handing this
    /// back is what lets the layout return the panel rather than build a new one
    /// around a new document — which would be a new panel, with a new session.
    /// </summary>
    public IDocument Document { get; }

    public double X
    {
        get => _x;
        private set => SetProperty(ref _x, value);
    }

    public double Y
    {
        get => _y;
        private set => SetProperty(ref _y, value);
    }

    public double Width
    {
        get => _width;
        private set => SetProperty(ref _width, value);
    }

    public double Height
    {
        get => _height;
        private set => SetProperty(ref _height, value);
    }

    public void MoveTo(double x, double y, Size within)
    {
        X = Clamp(x, within.Width, Width);
        Y = Clamp(y, within.Height, Height);
    }

    public void ResizeTo(double width, double height)
    {
        Width = Math.Max(240, width);
        Height = Math.Max(140, height);
    }

    private static double Clamp(double position, double available, double extent)
    {
        if (!double.IsFinite(available) || available <= 0)
        {
            return Math.Max(0, position);
        }

        return Math.Clamp(
            position,
            Math.Min(0, Reachable - extent),
            Math.Max(0, available - Reachable));
    }
}
