namespace GhostShell.Application;

public interface IDefinitionBundleStore
{
    ValueTask<DefinitionStoreResult<PortableDefinitionBundle>> ExportAsync(
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<DefinitionImportPreflight>> PreflightImportAsync(
        PortableDefinitionBundle bundle,
        DefinitionImportMode mode,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<DefinitionImportResult>> CommitImportAsync(
        DefinitionImportPreflight preflight,
        CancellationToken cancellationToken,
        DefinitionImportExecutionApproval? executionApproval = null);
}
