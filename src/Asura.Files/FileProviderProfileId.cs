namespace Asura.Files;

public readonly record struct FileProviderProfileId
{
    public FileProviderProfileId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException(
                "A file-provider profile ID may contain only ASCII letters, digits, '.', '_', and '-'.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
