using System.Globalization;
using System.Runtime.InteropServices;

namespace Asura.Core;

[StructLayout(LayoutKind.Auto)]
public readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public static RgbColor Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var hex = value[0] == '#' ? value[1..] : value;
        if (hex.Length != 6
            || !byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red)
            || !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green)
            || !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            throw new FormatException("An RGB color must contain exactly six hexadecimal digits.");
        }

        return new RgbColor(red, green, blue);
    }

    /// <summary>
    /// Parses without throwing, for editors that read a field while it is still
    /// being typed.
    /// </summary>
    public static bool TryParse(string? value, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hex = value[0] == '#' ? value[1..] : value;
        if (hex.Length != 6
            || !byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red)
            || !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green)
            || !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            return false;
        }

        color = new RgbColor(red, green, blue);
        return true;
    }

    public override string ToString() => $"#{Red:X2}{Green:X2}{Blue:X2}";
}
