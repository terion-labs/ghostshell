using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Asura.App.Views.Components;

/// <summary>
/// A picture that can be looked into: zoomed with the wheel or the toolbar,
/// dragged around once it is larger than the space it is shown in, and turned
/// a quarter at a time. Used for both image and PDF page previews, so the two
/// behave identically rather than each growing its own gestures.
/// </summary>
public sealed partial class ZoomableImageView : UserControl
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ZoomableImageView, Bitmap?>(nameof(Source));

    private const double MinimumScale = 0.05;
    private const double MaximumScale = 8d;
    private const double ZoomStep = 1.25;

    private double _scale = 1d;
    private double _fitScale = 1d;
    private int _angle;
    private Vector _offset;
    private bool _scaleChosenByHand;
    private PixelSize _sourceSize;
    private Point _dragOrigin;
    private Vector _dragOffset;
    private bool _dragging;

    public ZoomableImageView()
    {
        InitializeComponent();

        Viewport.PointerWheelChanged += OnPointerWheelChanged;
        Viewport.PointerPressed += OnPointerPressed;
        Viewport.PointerMoved += OnPointerMoved;
        Viewport.PointerReleased += OnPointerReleased;
        Viewport.DoubleTapped += OnDoubleTapped;
        Viewport.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                Refit(keepUserScale: true);
            }
        };
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceProperty)
        {
            return;
        }

        var source = Source;
        Surface.Source = source;

        // Sized explicitly rather than left to measure: an unsized image with
        // no stretch is clamped to the space available, which crops it to the
        // panel instead of letting the transform scale the whole picture.
        Surface.Width = source?.Size.Width ?? double.NaN;
        Surface.Height = source?.Size.Height ?? double.NaN;

        // A different picture starts fresh; the same-sized one does not, so
        // paging through a PDF keeps the zoom and rotation being read at.
        var size = source?.PixelSize ?? default;
        var sameShape = size == _sourceSize && size != default;
        _sourceSize = size;
        if (!sameShape)
        {
            _angle = 0;
            _offset = default;
            _scaleChosenByHand = false;
        }

        Refit(keepUserScale: sameShape);
    }

    /// <summary>Magnifies a step about the middle of the view.</summary>
    public void ZoomIn() => ZoomBy(ZoomStep, ViewportCentre);

    /// <summary>Pulls back a step about the middle of the view.</summary>
    public void ZoomOut() => ZoomBy(1 / ZoomStep, ViewportCentre);

    /// <summary>Returns to the whole picture, centred.</summary>
    public void FitToViewport()
    {
        _offset = default;
        _scaleChosenByHand = false;
        Refit(keepUserScale: false);
    }

    /// <summary>Turns the picture a quarter anticlockwise.</summary>
    public void RotateLeft() => Rotate(-90);

    /// <summary>Turns the picture a quarter clockwise.</summary>
    public void RotateRight() => Rotate(90);

    /// <summary>The magnification currently shown, 1 being actual size.</summary>
    public double Scale => _scale;

    /// <summary>The quarter turns applied, in degrees clockwise.</summary>
    public int Rotation => _angle;

    private void OnZoomInClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ZoomIn();

    private void OnZoomOutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ZoomOut();

    private void OnFitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        FitToViewport();

    private void OnRotateLeftClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        RotateLeft();

    private void OnRotateRightClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        RotateRight();

    private void Rotate(int degrees)
    {
        _angle = ((_angle + degrees) % 360 + 360) % 360;
        _offset = default;
        Refit(keepUserScale: _scaleChosenByHand);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Source is null)
        {
            return;
        }

        var steps = e.Delta.Y;
        if (Math.Abs(steps) < 0.01)
        {
            return;
        }

        ZoomBy(Math.Pow(ZoomStep, steps), e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Source is null || !e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _dragOrigin = e.GetPosition(Viewport);
        _dragOffset = _offset;
        Viewport.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(Viewport);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _offset = _dragOffset + (e.GetPosition(Viewport) - _dragOrigin);
        ApplyTransform();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Viewport.Cursor = new Cursor(StandardCursorType.Hand);
        e.Pointer.Capture(null);
    }

    private void OnDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        // Between fitted and actual size, the two readings of a picture worth
        // one gesture rather than a hunt for the right zoom level.
        if (Math.Abs(_scale - _fitScale) < 0.001)
        {
            ZoomBy(1 / _fitScale, e.GetPosition(Viewport));
        }
        else
        {
            FitToViewport();
        }
    }

    private Point ViewportCentre => new(Viewport.Bounds.Width / 2, Viewport.Bounds.Height / 2);

    private void ZoomBy(double factor, Point anchor)
    {
        var target = Math.Clamp(_scale * factor, MinimumScale, MaximumScale);
        if (Math.Abs(target - _scale) < 0.0001)
        {
            return;
        }

        _offset = ZoomableImageGeometry.AnchoredOffset(
            _offset, anchor, ViewportCentre, target / _scale);
        _scale = target;
        _scaleChosenByHand = true;
        ApplyTransform();
    }

    private void Refit(bool keepUserScale)
    {
        _fitScale = ZoomableImageGeometry.FitScale(
            _sourceSize.Width == 0
                ? default
                : new Size(_sourceSize.Width, _sourceSize.Height),
            Viewport.Bounds.Size,
            _angle);

        if (!keepUserScale || !_scaleChosenByHand)
        {
            _scale = _fitScale;
        }

        ApplyTransform();
    }

    private void ApplyTransform()
    {
        _offset = ZoomableImageGeometry.ClampOffset(
            _offset,
            _sourceSize.Width == 0 ? default : new Size(_sourceSize.Width, _sourceSize.Height),
            Viewport.Bounds.Size,
            _angle,
            _scale);

        Surface.RenderTransform = new MatrixTransform(
            Matrix.CreateRotation(_angle * Math.PI / 180)
            * Matrix.CreateScale(_scale, _scale)
            * Matrix.CreateTranslation(_offset.X, _offset.Y));

        ZoomLevelText.Text = $"{Math.Round(_scale * 100)}%";
        Toolbar.IsVisible = Source is not null;
    }
}

/// <summary>
/// The arithmetic behind the gestures, kept apart from the control so it can be
/// checked without a rendering platform.
/// </summary>
internal static class ZoomableImageGeometry
{
    /// <summary>
    /// The scale at which the whole picture is visible. Never above 1: a small
    /// image is shown at its own size rather than blown up to fill the space.
    /// </summary>
    public static double FitScale(Size content, Size viewport, int angle)
    {
        if (content.Width <= 0 || content.Height <= 0
            || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return 1d;
        }

        var (width, height) = IsQuarterTurned(angle)
            ? (content.Height, content.Width)
            : (content.Width, content.Height);

        return Math.Min(1d, Math.Min(viewport.Width / width, viewport.Height / height));
    }

    /// <summary>
    /// The offset that keeps whatever sits under the pointer sitting under it
    /// after a zoom, so the wheel magnifies what is being looked at rather than
    /// the middle of the panel.
    /// </summary>
    public static Vector AnchoredOffset(Vector offset, Point anchor, Point centre, double factor)
    {
        var fromCentre = anchor - centre;
        return fromCentre - ((fromCentre - offset) * factor);
    }

    /// <summary>
    /// Panning stops once an edge reaches the middle of the view, so a picture
    /// cannot be flung out of sight and lost.
    /// </summary>
    public static Vector ClampOffset(
        Vector offset,
        Size content,
        Size viewport,
        int angle,
        double scale)
    {
        if (content.Width <= 0 || content.Height <= 0)
        {
            return default;
        }

        var (width, height) = IsQuarterTurned(angle)
            ? (content.Height, content.Width)
            : (content.Width, content.Height);

        var limitX = ((width * scale) + viewport.Width) / 2;
        var limitY = ((height * scale) + viewport.Height) / 2;
        return new Vector(
            Math.Clamp(offset.X, -limitX, limitX),
            Math.Clamp(offset.Y, -limitY, limitY));
    }

    private static bool IsQuarterTurned(int angle) => angle is 90 or 270;
}
