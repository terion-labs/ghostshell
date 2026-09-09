namespace Asura.Application;

public sealed record SecretVaultAvailability(
    SecretVaultAvailabilityState State,
    SecretVaultPersistenceKind Persistence,
    SecretVaultCapabilities Capabilities,
    string Adapter,
    string DiagnosticCode,
    string Message)
{
    public bool CanPersist =>
        State is not SecretVaultAvailabilityState.Unavailable
        && Persistence == SecretVaultPersistenceKind.OsProtectedPersistent;
}
