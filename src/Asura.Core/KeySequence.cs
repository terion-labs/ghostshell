using System.Text.Json.Serialization;

namespace Asura.Core;

public sealed class KeySequence : IEquatable<KeySequence>
{
    private readonly IReadOnlyList<KeyStroke> _strokes;

    [JsonConstructor]
    public KeySequence(IReadOnlyList<KeyStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        _strokes = Array.AsReadOnly(strokes.ToArray());

        if (_strokes.Count == 0)
        {
            throw new ArgumentException("A key sequence requires at least one stroke.", nameof(strokes));
        }

        if (_strokes.Count > 8)
        {
            throw new ArgumentException("A key sequence cannot contain more than eight strokes.", nameof(strokes));
        }
    }

    public int Count => _strokes.Count;

    public KeyStroke this[int index] => _strokes[index];

    public IReadOnlyList<KeyStroke> Strokes => _strokes;

    public static KeySequence Of(params KeyStroke[] strokes) => new(strokes);

    public bool IsPrefixOf(KeySequence other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Count >= other.Count)
        {
            return false;
        }

        for (var index = 0; index < Count; index++)
        {
            if (this[index] != other[index])
            {
                return false;
            }
        }

        return true;
    }

    public bool Equals(KeySequence? other) => other is not null && _strokes.SequenceEqual(other._strokes);

    public override bool Equals(object? obj) => obj is KeySequence other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var stroke in _strokes)
        {
            hash.Add(stroke);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(", ", _strokes);

}
