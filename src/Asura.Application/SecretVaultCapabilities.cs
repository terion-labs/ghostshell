namespace Asura.Application;

[Flags]
public enum SecretVaultCapabilities
{
    None = 0,
    Create = 1 << 0,
    Resolve = 1 << 1,
    Replace = 1 << 2,
    Relabel = 1 << 3,
    Delete = 1 << 4,
    ReadMetadata = 1 << 5,
    ListMetadata = 1 << 6,
    All = Create | Resolve | Replace | Relabel | Delete | ReadMetadata | ListMetadata,
}
