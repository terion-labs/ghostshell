using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// Maps one HTTP header name to profile-scoped vault identity. Header values
/// are never represented in durable configuration.
/// </summary>
public sealed record McpServerHttpHeader
{
    public const int MaximumNameLength = 128;

    private static readonly HashSet<string> ReservedNames = new(
        [
            "Accept",
            "Connection",
            "Content-Length",
            "Content-Type",
            "Host",
            "Last-Event-ID",
            "MCP-Protocol-Version",
            "MCP-Session-Id",
            "Origin",
            "Transfer-Encoding",
        ],
        StringComparer.OrdinalIgnoreCase);

    [JsonConstructor]
    public McpServerHttpHeader(string name, SecretRef reference)
    {
        if (string.IsNullOrEmpty(name)
            || name.Length > MaximumNameLength
            || ReservedNames.Contains(name)
            || name.Any(character => !IsTokenCharacter(character)))
        {
            throw new ArgumentException(
                "An MCP HTTP header name must be a bounded, non-reserved RFC 9110 token.",
                nameof(name));
        }

        McpServerProfile.ValidateSecretReference(reference, nameof(reference));
        Name = name;
        Reference = reference;
    }

    public string Name { get; }

    public SecretRef Reference { get; }

    private static bool IsTokenCharacter(char character) =>
        character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '!'
            or '#'
            or '$'
            or '%'
            or '&'
            or '\''
            or '*'
            or '+'
            or '-'
            or '.'
            or '^'
            or '_'
            or '`'
            or '|'
            or '~';
}
