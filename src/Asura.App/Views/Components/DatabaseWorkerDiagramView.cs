using Asura.App.ViewModels;
using Asura.Application;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Asura.App.Views.Components;

/// <summary>Pan/zoom a full diagram without parsing schema or SVG in the UI process.</summary>
public sealed class DatabaseWorkerDiagramView : UserControl
{
    public static readonly StyledProperty<IDatabaseDiagramSession?> SessionProperty =
        AvaloniaProperty.Register<DatabaseWorkerDiagramView, IDatabaseDiagramSession?>(nameof(Session));

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _fit = new() { Content = "Fit" };
    private Bitmap? _bitmap;
    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragStart;
    private Vector _dragPan;
    private long _requested;
    private bool _rendering;
    private bool _attached;

    public DatabaseWorkerDiagramView()
    {
        Focusable = true;
        ClipToBounds = true;
        var grid = new Grid { Background = Brushes.Transparent };
        grid.Children.Add(_image);
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            Spacing = 4,
        };
        var smaller = new Button { Content = "−" };
        var larger = new Button { Content = "+" };
        smaller.Click += (_, _) => ZoomBy(0.8);
        larger.Click += (_, _) => ZoomBy(1.25);
        _fit.Click += (_, _) => Fit();
        toolbar.Children.Add(smaller);
        toolbar.Children.Add(_fit);
        toolbar.Children.Add(larger);
        grid.Children.Add(toolbar);
        _error.HorizontalAlignment = HorizontalAlignment.Center;
        _error.VerticalAlignment = VerticalAlignment.Center;
        _error.Margin = new Thickness(20);
        grid.Children.Add(_error);
        Content = grid;
        SizeChanged += (_, _) => RequestViewport();
        ActualThemeVariantChanged += (_, _) => RequestViewport();
        PointerWheelChanged += (_, args) =>
        {
            ZoomBy(Math.Pow(1.25, args.Delta.Y));
            args.Handled = true;
        };
        DoubleTapped += (_, _) => Fit();
        PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed && args.Source is not Button)
            {
                Focus();
                _dragStart = args.GetPosition(this);
                _dragPan = _pan;
                args.Pointer.Capture(this);
            }
        };
        PointerMoved += (_, args) =>
        {
            if (_dragStart is { } start)
            {
                _pan = _dragPan + args.GetPosition(this) - start;
                RequestViewport();
            }
        };
        PointerReleased += (_, args) =>
        {
            _dragStart = null;
            args.Pointer.Capture(null);
        };
        KeyDown += (_, args) =>
        {
            var action = DatabaseMermaidDiagramView.ResolveKeyboardAction(args.Key, args.KeyModifiers, args.KeySymbol);
            if (action == DatabaseDiagramKeyboardAction.Fit)
            {
                Fit();
            }
            else if (action != DatabaseDiagramKeyboardAction.None)
            {
                ZoomBy(action == DatabaseDiagramKeyboardAction.ZoomIn ? 1.25 : 0.8);
            }

            args.Handled = action != DatabaseDiagramKeyboardAction.None;
        };
    }

    public IDatabaseDiagramSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty)
        {
            _image.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
            Fit();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (DataContext is DatabaseRuntimePanelViewModel { IsDatabaseDiagramOverview: true, HasMermaidDiagram: false } panel)
        {
            _ = panel.ShowDatabaseDiagramAsync();
        }
        RequestViewport();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (DataContext is DatabaseRuntimePanelViewModel panel)
        {
            panel.SuspendDatabaseDiagram();
        }
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Fit()
    {
        _zoom = 1;
        _pan = default;
        _fit.Content = "Fit";
        RequestViewport();
    }

    private void ZoomBy(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 0.1, 100);
        _fit.Content = $"{Math.Round(_zoom * 100)}%";
        RequestViewport();
    }

    private void RequestViewport()
    {
        _requested++;
        if (!_rendering && _attached && Session is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            _ = RenderLatestAsync();
        }
    }

    private async Task RenderLatestAsync()
    {
        var originalSession = Session;
        _rendering = true;
        try
        {
            long rendered;
            do
            {
                rendered = _requested;
                var session = Session;
                if (!_attached || session is null)
                {
                    return;
                }

                // Coalesce input while one frame is rendering; never queue a
                // bitmap for every pointer event or cancel the whole worker.
                var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
                var width = Math.Clamp((int)Math.Ceiling(Bounds.Width * scale), 1, 4096);
                var height = Math.Clamp((int)Math.Ceiling(Bounds.Height * scale), 1, 4096);
                var png = await session.RenderViewportAsync(
                    new DatabaseDiagramViewport(width, height, _zoom, _pan.X * scale, _pan.Y * scale,
                        ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light),
                    CancellationToken.None);
                using var stream = new MemoryStream(png, writable: false);
                var bitmap = new Bitmap(stream);
                if (!_attached || !ReferenceEquals(session, Session))
                {
                    bitmap.Dispose();
                    continue;
                }

                var old = _bitmap;
                _bitmap = bitmap;
                _image.Source = bitmap;
                old?.Dispose();
                _error.IsVisible = false;
            }
            while (rendered != _requested);
        }
        catch (Exception)
        {
            _error.Text = "The diagram worker stopped. Reopen the diagram to render it again.";
            _error.IsVisible = Session is not null;
        }
        finally
        {
            _rendering = false;
            if (!ReferenceEquals(originalSession, Session))
            {
                RequestViewport();
            }
        }
    }
}
