using System.Security.Cryptography;
using System.Text;

namespace Asura.Application;

/// <summary>
/// A bounded correlation digest. The underlying material is deliberately not
/// retained by authorization or audit records.
/// </summary>
public readonly record struct AgentActionDigest
{
    public const int EncodedLength = SHA256.HashSizeInBytes * 2;

    public AgentActionDigest(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != EncodedLength
            || value.Any(character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "An agent action digest must be a lowercase SHA-256 value.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static AgentActionDigest FromUtf8(string material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var bytes = Encoding.UTF8.GetBytes(material);
        try
        {
            return new AgentActionDigest(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public override string ToString() => Value;
}
