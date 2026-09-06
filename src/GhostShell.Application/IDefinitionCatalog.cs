using GhostShell.Core;

namespace GhostShell.Application;

public interface IDefinitionCatalog
{
    DefinitionCatalogSnapshot Snapshot { get; }

    event EventHandler? Changed;

    ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
        ConnectionProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutAsync(
        LayoutDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveScreenAsync(
        ScreenDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Saves one database connection profile. The default fails for catalogs
    /// without database-connection support; <see cref="DefinitionCatalog"/>
    /// implements it.
    /// </summary>
    ValueTask<DefinitionStoreResult<StoredDefinition<DatabaseConnectionProfile>>>
        SaveDatabaseConnectionAsync(
            DatabaseConnectionProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            DefinitionStoreResult<StoredDefinition<DatabaseConnectionProfile>>.Failure(new(
                DefinitionStoreErrorCode.UnsupportedKind,
                "This catalog cannot store database connections.")));

    ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
        WorkspaceDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Saves a workspace together with the auto-saved tab layouts its entries
    /// reference, as one atomic batch. Returns null on success. The default
    /// implementation composes the individual saves for catalogs without a
    /// transactional store; <see cref="DefinitionCatalog"/> overrides it with a
    /// single validated batch.
    /// </summary>
    async ValueTask<DefinitionStoreError?> SaveWorkspaceWithLayoutsAsync(
        WorkspaceDefinition workspace,
        long? expectedWorkspaceRevision,
        IReadOnlyList<(LayoutDefinition Definition, long? ExpectedRevision)> layouts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        foreach (var (layout, expectedRevision) in layouts)
        {
            var layoutResult = await SaveLayoutAsync(layout, expectedRevision, cancellationToken)
                .ConfigureAwait(false);
            if (!layoutResult.IsSuccess)
            {
                return layoutResult.Error;
            }
        }

        var workspaceResult = await SaveWorkspaceAsync(
                workspace,
                expectedWorkspaceRevision,
                cancellationToken)
            .ConfigureAwait(false);
        return workspaceResult.IsSuccess ? null : workspaceResult.Error;
    }

    ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
        ThemePreference definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
        TerminalProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
        KeymapProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> SaveFileProviderProfileAsync(
        FileProviderProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> SaveAiProviderProfileAsync(
        AiProviderProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>> SaveMcpServerProfileAsync(
        McpServerProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<BrowserProfileDefinition>>> SaveBrowserProfileAsync(
        BrowserProfileDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            DefinitionStoreResult<StoredDefinition<BrowserProfileDefinition>>.Failure(new(
                DefinitionStoreErrorCode.UnsupportedKind,
                "This catalog cannot store browser profiles.")));

    ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
        QuickTerminalSettings definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<NetworkConnectionProfile>>> SaveNetworkConnectionAsync(
        NetworkConnectionProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            DefinitionStoreResult<StoredDefinition<NetworkConnectionProfile>>.Failure(new(
                DefinitionStoreErrorCode.UnsupportedKind,
                "This catalog cannot store network connections.")));

    ValueTask<DefinitionStoreResult<StoredDefinition<ApplicationNetworkSettings>>>
        SaveApplicationNetworkSettingsAsync(
            ApplicationNetworkSettings definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            DefinitionStoreResult<StoredDefinition<ApplicationNetworkSettings>>.Failure(new(
                DefinitionStoreErrorCode.UnsupportedKind,
                "This catalog cannot store application network settings.")));

    ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long expectedRevision,
        CancellationToken cancellationToken);
}
