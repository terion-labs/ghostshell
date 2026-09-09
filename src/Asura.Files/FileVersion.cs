namespace Asura.Files;

public readonly record struct FileVersion
{
    public FileVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 512 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A file version must be a bounded opaque value.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
