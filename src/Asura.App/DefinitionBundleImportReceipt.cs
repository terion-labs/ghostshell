using Asura.Application;

namespace Asura.App;

/// <summary>
/// Separates the durable import result from presentation reload status. A reload failure never
/// misreports an already-committed import as rolled back.
/// </summary>
public sealed record DefinitionBundleImportReceipt(
    int Inserted,
    int Replaced,
    DefinitionStoreError? ReloadError,
    bool WorkspacesRemainUnavailable = false)
{
    public bool CatalogReloaded => ReloadError is null;
}
