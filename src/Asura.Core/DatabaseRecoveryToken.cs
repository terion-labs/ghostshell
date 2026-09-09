namespace Asura.Core;

/// <summary>A device-local vault reference, never a database address.</summary>
public sealed record DatabaseRecoveryToken(string OwnerId, SecretRef Reference)
{
    public const string Prefix = "database-vault-v1:";
    public const string ReconnectTarget = "database-reconnect-required";

    public string Serialize() => $"{Prefix}{OwnerId}:{Reference.Value}";

    public static DatabaseRecoveryToken Create() => new(Guid.NewGuid().ToString("N"), SecretRef.New());

    public static DatabaseRecoveryToken? TryParse(string? target)
    {
        if (target?.StartsWith(Prefix, StringComparison.Ordinal) != true)
        {
            return null;
        }
        var parts = target[Prefix.Length..].Split(':');
        return parts.Length == 2
            && Guid.TryParseExact(parts[0], "N", out _)
            && Guid.TryParseExact(parts[1], "N", out _)
                ? new DatabaseRecoveryToken(parts[0], new SecretRef(parts[1]))
                : null;
    }
}
