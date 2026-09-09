namespace Asura.Application;

public readonly record struct AgentFileEntryReference
{
    public const int EncodedLength = 43;

    public AgentFileEntryReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != EncodedLength || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException(
                "The file-entry reference must be an opaque base64url value.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
