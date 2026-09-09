using System.Text.Json.Serialization;

namespace Asura.Core;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    Meta = 1 << 3,
}

public readonly record struct KeyStroke
{
    private const KeyModifiers AllModifiers = KeyModifiers.Control
        | KeyModifiers.Alt
        | KeyModifiers.Shift
        | KeyModifiers.Meta;

    [JsonConstructor]
    public KeyStroke(string key, KeyModifiers modifiers = KeyModifiers.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if ((modifiers & ~AllModifiers) != KeyModifiers.None)
        {
            throw new ArgumentOutOfRangeException(nameof(modifiers), modifiers, "The key stroke contains an unknown modifier.");
        }

        Key = key.Trim().ToUpperInvariant();
        Modifiers = modifiers;
    }

    public string Key { get; }

    public KeyModifiers Modifiers { get; }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & KeyModifiers.Control) != KeyModifiers.None)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & KeyModifiers.Alt) != KeyModifiers.None)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & KeyModifiers.Shift) != KeyModifiers.None)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & KeyModifiers.Meta) != KeyModifiers.None)
        {
            parts.Add("Meta");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }
}
