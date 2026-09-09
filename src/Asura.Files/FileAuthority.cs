namespace Asura.Files;

public readonly record struct FileAuthority
{
    public FileAuthority(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 255 || value.Any(character => character is '\0' or '/' or '\\' || char.IsControl(character)))
        {
            throw new ArgumentException(
                "A file authority must be an opaque name without path separators or control characters.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
