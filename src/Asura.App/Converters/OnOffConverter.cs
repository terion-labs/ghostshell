using System.Globalization;
using Avalonia.Data.Converters;

namespace Asura.App.Converters;

/// <summary>
/// States a switch's position as "On" or "Off". Binding a bool straight to text
/// renders "True", which is the value's type rather than its meaning.
/// </summary>
public sealed class OnOffConverter : IValueConverter
{
    public static OnOffConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        value is bool state ? (state ? "On" : "Off") : string.Empty;

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException("Switch state text is one-way; bind IsChecked to the value.");
}
