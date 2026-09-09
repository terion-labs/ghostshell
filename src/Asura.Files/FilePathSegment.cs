namespace Asura.Files;

public readonly record struct FilePathSegment
{
    public FilePathSegment(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value is "." or "..")
        {
            throw new ArgumentException("Traversal segments are not valid file path segments.", nameof(value));
        }

        if (value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A file path segment cannot contain '/' or a null character.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
