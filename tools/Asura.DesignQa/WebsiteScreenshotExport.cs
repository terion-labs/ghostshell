using Asura.App.Views;
using Asura.Application;
using Asura.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImageMagick;
using ImageMagick.Drawing;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Asura.DesignQa;

/// <summary>
/// Turns deterministic QA routes into website-ready window assets. The app
/// supplies the pixels inside the frame; this boundary supplies only chrome
/// that the headless platform cannot draw and the alpha mask a browser needs.
/// </summary>
internal static class WebsiteScreenshotExport
{
    public const int LogicalWidth = 1440;
    public const int LogicalHeight = 900;
    public const int Scale = 2;
    public const int PixelWidth = LogicalWidth * Scale;
    public const int PixelHeight = LogicalHeight * Scale;
    private const int RasterDpi = 96;

    // The product enum calls the middle density "Cozy"; the UI calls it
    // "Normal". Website artwork always uses that exact setting, never the
    // 1.22x Spacious setting.
    private const InterfaceDensity NormalDensity = InterfaceDensity.Cozy;
    private const double NormalTextScale = 1;

    private const double TrafficLightDiameter = 12;
    private const double TrafficLightTop = 16;
    private const double TrafficLightFirstLeft = 18;
    private const double TrafficLightSpacing = 20;
    private const int DialogInset = 72;

    private static readonly double CornerRadius =
        DensityCornerScale.WindowRadius(NormalDensity);

    public static ThemePreference NormalizeTheme(ThemePreference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ThemePreference(
            source.Id,
            source.Name,
            source.Appearance,
            source.PlatformProfile,
            AccentPreference.AsuraBronze,
            NormalTextScale,
            NormalDensity,
            showTabBar: true,
            source.ShowWorkspacesPanel,
            TabStripPlacement.Top,
            source.WorkspacePanelPlacement,
            isTranslucent: true,
            source.BackdropOpacityPercent,
            source.HasGlassPanels,
            overridesBackdropOpacity: true);
    }

    public static DefinitionCatalogSnapshot NormalizeSnapshot(
        DefinitionCatalogSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source with
        {
            Themes =
            [
                .. source.Themes.Select(stored =>
                    stored with { Value = NormalizeTheme(stored.Value) }),
            ],
        };
    }

    public static void PrepareWindow(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.DataTemplates.Add(
            new FuncDataTemplate<WebsiteDummyRuntimePanelViewModel>(
                static (panel, _) => new WebsiteDummyRuntimePanelView
                {
                    DataContext = panel,
                }));
        window.Background = Brushes.Transparent;
        window.Width = LogicalWidth;
        window.Height = LogicalHeight;

        if (window.Content is not Panel shell)
        {
            throw new InvalidOperationException(
                "Website export requires the MainWindow panel root.");
        }

        var trafficLights = new Canvas
        {
            Width = 82,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        trafficLights.Children.Add(TrafficLight("#FF5F57", 0));
        trafficLights.Children.Add(TrafficLight("#FEBC2E", 1));
        trafficLights.Children.Add(TrafficLight("#28C840", 2));
        trafficLights.SetValue(Panel.ZIndexProperty, int.MaxValue);
        shell.Children.Add(trafficLights);
    }

    public static void WriteFrame(Control target, string path)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var bytes = RenderScaled(
            target,
            new PixelSize(LogicalWidth, LogicalHeight));
        using var frame = new MagickImage(bytes);
        ApplyWindowShape(frame);
        frame.Format = MagickFormat.Png;
        frame.Write(path);
    }

    public static void WriteDialogFrame(
        MainWindow backdrop,
        Control dialog,
        PixelSize logicalDialogSize,
        string path)
    {
        ArgumentNullException.ThrowIfNull(backdrop);
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var backdropBytes = RenderScaled(
            backdrop,
            new PixelSize(LogicalWidth, LogicalHeight));
        using var dialogBytes = RenderScaled(dialog, logicalDialogSize);
        using var frame = new MagickImage(backdropBytes);
        using var dialogImage = new MagickImage(dialogBytes);

        FitDialog(dialogImage);
        var x = ((int)frame.Width - (int)dialogImage.Width) / 2;
        var y = ((int)frame.Height - (int)dialogImage.Height) / 2;
        frame.Composite(dialogImage, x, y, CompositeOperator.Over);
        ApplyWindowShape(frame);
        frame.Format = MagickFormat.Png;
        frame.Write(path);
    }

    public static void WriteChromeMask(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        using var mask = CreateWindowMask();
        mask.Format = MagickFormat.Png;
        var path = Path.Combine(outputDirectory, "window-chrome-mask.png");
        mask.Write(path);
        Console.WriteLine($"MASK window chrome -> {path} ({PixelWidth}x{PixelHeight})");
    }

    public static bool IncludesRoute(string name) => name switch
    {
        // These are QA comparisons of alternate sizes, densities, focus, or
        // chrome. They are useful to the product suite but are not app screens.
        "settings-security" or
        "workspace-docker-narrow" or
        "workspace-git-narrow" or
        "settings-appearance-focused" or
        "settings-appearance-density-compact" or
        "settings-appearance-full" or
        "settings-terminal-full" or
        "appearance-corners-tight" or
        "appearance-corners-round" or
        "design-system-high-contrast" or
        "design-system-scale-200" or
        "design-system-scale-250" or
        "workspace-tabs-side" => false,
        _ => true,
    };

    private static Ellipse TrafficLight(string color, int index)
    {
        var light = new Ellipse
        {
            Width = TrafficLightDiameter,
            Height = TrafficLightDiameter,
            Fill = new SolidColorBrush(Color.Parse(color)),
            Stroke = new SolidColorBrush(Color.Parse("#33000000")),
            StrokeThickness = 0.75,
        };
        Canvas.SetLeft(light, TrafficLightFirstLeft + (TrafficLightSpacing * index));
        Canvas.SetTop(light, TrafficLightTop);
        return light;
    }

    private static MemoryStream Render(Control target, PixelSize size, Vector dpi)
    {
        var bytes = new MemoryStream();
        using var bitmap = new RenderTargetBitmap(size, dpi);
        bitmap.Render(target);
        bitmap.Save(bytes);
        bytes.Position = 0;
        return bytes;
    }

    private static MemoryStream RenderScaled(Control target, PixelSize logicalSize)
    {
        var pixelSize = new PixelSize(
            logicalSize.Width * Scale,
            logicalSize.Height * Scale);
        // Rendering the 1x visual tree straight into a 192-DPI bitmap makes
        // Avalonia apply some centered-child offsets twice. A VisualBrush
        // instead redraws that already-laid-out tree onto a true 2x surface,
        // preserving both geometry and vector-quality text and strokes.
        var scaledFrame = new Border
        {
            Width = pixelSize.Width,
            Height = pixelSize.Height,
            Background = new VisualBrush(target)
            {
                Stretch = Stretch.Fill,
            },
        };
        scaledFrame.Measure(new Size(pixelSize.Width, pixelSize.Height));
        scaledFrame.Arrange(new Rect(0, 0, pixelSize.Width, pixelSize.Height));
        return Render(scaledFrame, pixelSize, new Vector(RasterDpi, RasterDpi));
    }

    private static void FitDialog(MagickImage dialog)
    {
        var maximumWidth = (uint)(PixelWidth - (DialogInset * Scale * 2));
        var maximumHeight = (uint)(PixelHeight - (DialogInset * Scale * 2));
        if (dialog.Width <= maximumWidth && dialog.Height <= maximumHeight)
        {
            return;
        }

        dialog.Resize(new MagickGeometry(maximumWidth, maximumHeight)
        {
            IgnoreAspectRatio = false,
            Greater = true,
        });
    }

    private static void ApplyWindowShape(MagickImage image)
    {
        if (image.Width != PixelWidth || image.Height != PixelHeight)
        {
            throw new InvalidOperationException(
                $"Website frames must be {PixelWidth}x{PixelHeight}; got {image.Width}x{image.Height}.");
        }

        using var mask = CreateWindowMask();
        image.Alpha(AlphaOption.Set);
        image.Composite(mask, CompositeOperator.DstIn);
    }

    private static MagickImage CreateWindowMask()
    {
        var mask = new MagickImage(MagickColors.Transparent, PixelWidth, PixelHeight);
        new Drawables()
            .FillColor(MagickColors.White)
            .StrokeColor(MagickColors.Transparent)
            .RoundRectangle(
                0,
                0,
                PixelWidth - 1,
                PixelHeight - 1,
                CornerRadius * Scale,
                CornerRadius * Scale)
            .Draw(mask);
        return mask;
    }
}
