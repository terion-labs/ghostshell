namespace Asura.Application;

/// <summary>
/// Opaque continuation state issued by the audit reader. Callers may retain
/// and return the value, but cannot select an audit position directly.
/// </summary>
public sealed record AgentRunAuditCursor
{
    public const int MaximumEncodedLength = 192;

    public AgentRunAuditCursor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumEncodedLength
            || value.Any(character =>
                character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                "An agent-run audit cursor must be bounded base64url text.",
                nameof(value));
        }

        Value = string.Concat(value);
    }

    public string Value { get; }
}
