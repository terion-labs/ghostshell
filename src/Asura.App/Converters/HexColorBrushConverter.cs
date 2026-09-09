using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Asura.App.Converters;

/// <summary>
/// Paints a swatch from the hex string a palette field holds. Colour fields are
/// edited as text, so the swatch has to render whatever is typed — including
/// half-finished values — without throwing. An unparseable value shows nothing
/// rather than a stale or wrong colour.
/// </summary>
public sealed class HexColorBrushConverter : IValueConverter
{
    public static HexColorBrushConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        value is string text && Color.TryParse(text, out var color)
            ? new SolidColorBrush(color)
            : Brushes.Transparent;

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException("Swatch brushes are one-way; edit the hex value instead.");
}
