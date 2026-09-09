namespace Asura.Files;

public readonly record struct FilePageToken
{
    public FilePageToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A file page token must be a bounded opaque value.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
