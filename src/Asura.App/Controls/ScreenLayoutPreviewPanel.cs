using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace Asura.App.Controls;

/// <summary>
/// Arranges a saved screen's real normalized layout inside a compact launcher preview.
/// </summary>
public sealed class ScreenLayoutPreviewPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : 240,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 64);
        foreach (var child in Children)
        {
            child.Measure(LayoutBounds(child, size).Size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.Arrange(LayoutBounds(child, finalSize));
        }

        return finalSize;
    }

    private static Rect LayoutBounds(Control child, Size size)
    {
        if (child.DataContext is not LauncherScreenPanelPreviewViewModel panel)
        {
            return new Rect(size);
        }

        var columns = Math.Max(1, panel.Columns);
        var rows = Math.Max(1, panel.Rows);
        var cellWidth = size.Width / columns;
        var cellHeight = size.Height / rows;
        return new Rect(
            panel.Column * cellWidth,
            panel.Row * cellHeight,
            panel.ColumnSpan * cellWidth,
            panel.RowSpan * cellHeight);
    }
}
