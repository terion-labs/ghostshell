namespace Asura.Application;

public sealed record SecretScope
{
    public SecretScope(SecretScopeKind kind, string? ownerId = null)
    {
        if (kind == SecretScopeKind.Global)
        {
            if (!string.IsNullOrWhiteSpace(ownerId))
            {
                throw new ArgumentException("A global secret scope cannot have an owner ID.", nameof(ownerId));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        }

        Kind = kind;
        OwnerId = ownerId;
    }

    public SecretScopeKind Kind { get; }

    public string? OwnerId { get; }

    public static SecretScope Global { get; } = new(SecretScopeKind.Global);
}
