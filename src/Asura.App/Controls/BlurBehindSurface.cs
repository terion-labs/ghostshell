using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Asura.App.Controls;

/// <summary>
/// Blurs whatever the window has already painted beneath this control, live.
///
/// Avalonia has no built-in blur-behind for in-window content, but its Skia
/// compositor paints the window back-to-front into one surface — so by the
/// time this control renders, everything under it is already on the canvas.
/// The draw operation snapshots that canvas, redraws the region under its own
/// bounds through a blur, and does it again on every frame the region is
/// repainted. Nothing is captured ahead of time, so there is nothing to go
/// stale and nothing to mis-align.
///
/// What never blurs: content that does not pass through the compositor at
/// all — native surfaces such as webviews — because it is not on the canvas
/// to be photographed.
/// </summary>
public sealed class BlurBehindSurface : Control
{
    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<BlurBehindSurface, double>(nameof(BlurRadius), 24);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<BlurBehindSurface, CornerRadius>(nameof(CornerRadius));

    /// <summary>
    /// The colour standing under the blur. On a translucent window the pixels
    /// beneath include the see-through backdrop, and blurring them near an
    /// edge yields partially transparent output — the surface visibly fades
    /// out where it should end. Grounding the blur on this fill instead
    /// resolves that transparency to a colour of ours.
    /// </summary>
    public static readonly StyledProperty<IBrush?> BaseBrushProperty =
        AvaloniaProperty.Register<BlurBehindSurface, IBrush?>(nameof(BaseBrush));

    static BlurBehindSurface()
    {
        AffectsRender<BlurBehindSurface>(
            BlurRadiusProperty,
            CornerRadiusProperty,
            BaseBrushProperty);
    }

    public double BlurRadius
    {
        get => GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public IBrush? BaseBrush
    {
        get => GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    private bool _framePending;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestLiveFrame();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            RequestLiveFrame();
        }
    }

    /// <summary>
    /// Nothing tells this control when the content beneath it changes, and a
    /// change repaints only its own little rectangle — a blur replayed inside
    /// that clip alone leaves the stale blur around it standing in visible
    /// bands. While on screen, the surface asks for a frame after every
    /// frame, so its whole region always repaints coherently.
    ///
    /// Effective visibility, not the control's own flag: a surface whose
    /// ancestor collapsed still says IsVisible, and a loop that trusted that
    /// once ran forever behind a hidden diagram — a 60fps invalidation churn
    /// that also presented mid-layout frames nothing should ever have shown.
    /// The loop ends the moment the surface stops being effectively on
    /// screen, and <see cref="Render"/> — which runs exactly when it comes
    /// back — is what restarts it.
    /// </summary>
    private void RequestLiveFrame()
    {
        if (_framePending
            || !IsEffectivelyVisible
            || VisualRoot is null
            || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        _framePending = true;
        topLevel.RequestAnimationFrame(_ =>
        {
            _framePending = false;
            if (IsEffectivelyVisible && VisualRoot is not null)
            {
                InvalidateVisual();
                RequestLiveFrame();
            }
        });
    }

    public override void Render(DrawingContext context)
    {
        context.Custom(new BlurBehindRenderOperation(
            new Rect(Bounds.Size),
            BlurRadius,
            CornerRadius.TopLeft,
            BaseBrush is ISolidColorBrush solid
                ? new SKColor(
                    solid.Color.R,
                    solid.Color.G,
                    solid.Color.B,
                    solid.Color.A)
                : null));
        RequestLiveFrame();
    }

    private sealed class BlurBehindRenderOperation(
        Rect operationBounds,
        double blurRadius,
        double cornerRadius,
        SKColor? baseColor) : ICustomDrawOperation
    {
        private readonly Rect _bounds = operationBounds;
        private readonly double _blurRadius = blurRadius;
        private readonly double _cornerRadius = cornerRadius;
        private readonly SKColor? _baseColor = baseColor;

        public Rect Bounds => _bounds;

        public void Dispose()
        {
        }

        public bool HitTest(Point p) => _bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) =>
            other is BlurBehindRenderOperation operation
            && operation._bounds == _bounds
            && Math.Abs(operation._blurRadius - _blurRadius) < 0.01
            && Math.Abs(operation._cornerRadius - _cornerRadius) < 0.01
            && operation._baseColor == _baseColor;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } leaseFeature)
            {
                return;
            }

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            if (lease.SkSurface is not { } windowSurface
                || !canvas.TotalMatrix.TryInvert(out var deviceToLocal))
            {
                return;
            }

            // Everything painted so far this frame, sampled in this control's
            // own coordinates: the inverse of the canvas transform maps local
            // points back onto the device pixels beneath them.
            using var behind = windowSurface.Snapshot();
            using var behindShader = SKShader.CreateImage(
                behind,
                SKShaderTileMode.Clamp,
                SKShaderTileMode.Clamp,
                deviceToLocal);

            var info = new SKImageInfo(
                (int)Math.Ceiling(_bounds.Width),
                (int)Math.Ceiling(_bounds.Height),
                SKImageInfo.PlatformColorType,
                SKAlphaType.Premul);
            if (info.Width <= 0 || info.Height <= 0)
            {
                return;
            }

            using var blurred = lease.GrContext is { } gpu
                ? SKSurface.Create(gpu, false, info)
                : SKSurface.Create(info);
            if (blurred is null)
            {
                return;
            }

            using (var blur = SKImageFilter.CreateBlur(
                (float)_blurRadius,
                (float)_blurRadius,
                SKShaderTileMode.Clamp))
            using (var blurPaint = new SKPaint())
            {
                blurPaint.Shader = behindShader;
                blurPaint.ImageFilter = blur;
                blurred.Canvas.DrawRect(
                    0,
                    0,
                    (float)_bounds.Width,
                    (float)_bounds.Height,
                    blurPaint);
            }

            using var blurredImage = blurred.Snapshot();
            using var paint = new SKPaint();
            paint.IsAntialias = true;
            canvas.Save();
            canvas.ClipRoundRect(
                new SKRoundRect(
                    SKRect.Create((float)_bounds.Width, (float)_bounds.Height),
                    (float)_cornerRadius),
                SKClipOperation.Intersect,
                antialias: true);
            if (_baseColor is { } ground)
            {
                using var groundPaint = new SKPaint();
                groundPaint.Color = ground;
                canvas.DrawRect(
                    SKRect.Create((float)_bounds.Width, (float)_bounds.Height),
                    groundPaint);
            }

            canvas.DrawImage(
                blurredImage,
                SKRect.Create((float)_bounds.Width, (float)_bounds.Height),
                paint);
            canvas.Restore();
        }
    }
}
