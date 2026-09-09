using Avalonia.Svg.Skia;
using Mermaider;
using Mermaider.Models;
using SkiaSharp;

namespace Asura.Desktop;

/// <summary>Worker-only vector state; even theme relayout stays outside the UI process.</summary>
internal sealed class DatabaseDiagramPicture : IDisposable
{
    private readonly string _source;
    private SvgSource? _picture;
    private bool _dark;

    public DatabaseDiagramPicture(string source)
    {
        _source = source;
        _ = ForTheme(true);
    }

    public string Svg { get; private set; } = string.Empty;

    public SKPicture ForTheme(bool dark)
    {
        if (_picture is null || _dark != dark)
        {
            var svg = MermaidRenderer.RenderSvg(_source, new RenderOptions
            {
                AllowedDiagrams = DiagramTypes.All,
                SanitizeMode = SanitizeMode.Block,
                Transparent = true,
                Fg = dark ? "#FFFFFF" : "#111111",
                Line = dark ? "#B8B9B6" : "#444444",
                Surface = dark ? "#1A1A1A" : "#F5F5F5",
                Border = dark ? "#888888" : "#666666",
                Accent = "#FF8400",
                Font = "Inter",
                FontSize = "13px",
            });
            var picture = SvgSource.LoadFromSvg(svg);
            if (picture.Picture is null)
            {
                picture.Dispose();
                throw new InvalidDataException("The diagram contains no picture.");
            }

            _picture?.Dispose();
            _picture = picture;
            _dark = dark;
            Svg = svg;
        }

        return _picture.Picture!;
    }

    public void Dispose() => _picture?.Dispose();
}
