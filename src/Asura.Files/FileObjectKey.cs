namespace Asura.Files;

/// <summary>
/// Preserves an object provider's exact key. Provider adapters validate encoding and length;
/// delimiters and dot segments remain data rather than hierarchical traversal instructions.
/// </summary>
public readonly record struct FileObjectKey
{
    public FileObjectKey(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
