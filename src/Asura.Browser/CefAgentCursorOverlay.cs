using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Asura.Browser;

/// <summary>
/// Presents the retained agent pointer above the native browser. Position
/// changes follow acknowledged Chromium mouse events, while visibility follows
/// the browser panel's authoritative agent-activity lease.
/// </summary>
internal sealed class CefAgentCursorOverlay : Canvas
{
    private const double CursorExtent = 34;
    private static readonly Color FallbackAccent = Color.Parse("#FFFF8400");

    private readonly FluentIcon _cursor;
    private readonly DropShadowEffect _shadow;
    private CefCursorPoint _position;
    private Color _accentColor;
    private bool _hasPosition;
    private bool _isAgentActive;

    public CefAgentCursorOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        IsVisible = false;

        _accentColor = FallbackAccent;
        _shadow = new DropShadowEffect
        {
            BlurRadius = 8,
            Color = Colors.Black,
            OffsetX = 1,
            OffsetY = 2,
            Opacity = 0.55,
        };
        _cursor = new FluentIcon
        {
            Effect = _shadow,
            FontSize = 31,
            Foreground = new SolidColorBrush(_accentColor),
            Height = CursorExtent,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Icon = Icon.Cursor,
            IconSize = IconSize.Size32,
            IconVariant = IconVariant.Filled,
            IsHitTestVisible = false,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Width = CursorExtent,
        };
        Children.Add(_cursor);
        ResourcesChanged += (_, _) => RefreshAccent();
    }

    public void ShowAt(CefCursorPoint point)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyPosition(point);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyPosition(point));
    }

    public void SetAgentActivity(bool isActive)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetAgentActivity(isActive));
            return;
        }

        _isAgentActive = isActive;
        IsVisible = isActive && _hasPosition;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        PositionCursor(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    private void ApplyPosition(CefCursorPoint point)
    {
        _position = point;
        _hasPosition = true;
        RefreshAccent();
        PositionCursor(Bounds.Size);
        IsVisible = _isAgentActive;
    }

    private void PositionCursor(Size available)
    {
        SetLeft(
            _cursor,
            Math.Clamp(
                _position.X,
                0,
                Math.Max(0, available.Width - CursorExtent)));
        SetTop(
            _cursor,
            Math.Clamp(
                _position.Y,
                0,
                Math.Max(0, available.Height - CursorExtent)));
    }

    private void RefreshAccent()
    {
        var accent = ResolveAccentColor();
        if (accent == _accentColor)
        {
            return;
        }

        _accentColor = accent;
        _cursor.Foreground = new SolidColorBrush(accent);
    }

    private Color ResolveAccentColor()
    {
        if (!this.TryFindResource(
                "ShellAccentBrush",
                ActualThemeVariant,
                out var resource)
            || resource is not ISolidColorBrush solid)
        {
            return FallbackAccent;
        }

        return solid.Color;
    }
}
