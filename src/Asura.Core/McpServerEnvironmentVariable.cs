using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Asura.Core;

/// <summary>
/// Maps one portable environment-variable name to opaque vault identity.
/// Plaintext environment values are intentionally not representable.
/// </summary>
public sealed partial record McpServerEnvironmentVariable
{
    public const int MaximumNameLength = 128;
    private const int RegexTimeoutMilliseconds = 1_000;

    [JsonConstructor]
    public McpServerEnvironmentVariable(string name, SecretRef reference)
    {
        if (string.IsNullOrEmpty(name)
            || name.Length > MaximumNameLength
            || !PortableName().IsMatch(name))
        {
            throw new ArgumentException(
                "An MCP environment variable name must use bounded portable identifier syntax.",
                nameof(name));
        }

        McpServerProfile.ValidateSecretReference(reference, nameof(reference));
        Name = name;
        Reference = reference;
    }

    public string Name { get; }

    public SecretRef Reference { get; }

    [GeneratedRegex(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex PortableName();
}
