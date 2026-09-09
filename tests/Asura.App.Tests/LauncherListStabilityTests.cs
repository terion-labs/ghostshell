using System.Collections.Specialized;
using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

/// <summary>
/// Every launcher list was cleared and refilled on every catalog notification, and
/// most notifications are provoked by something the list does not show. Refilling
/// destroys every realized row, so the row under the pointer lost its hover state
/// and immediately regained it — the whole page appeared to flicker while the
/// pointer sat still.
///
/// These tests pin the two halves of the fix: republishing an unchanged catalog
/// leaves the collections untouched, and a real edit still comes through.
/// </summary>
public sealed class LauncherListStabilityTests
{
    [Fact]
    public void Refreshing_from_an_unchanged_catalog_does_not_disturb_the_lists()
    {
        using var viewModel = CreateViewModel(new FixedCatalog(Snapshot("Prod web")));
        var connection = Assert.Single(viewModel.Connections);
        var screen = Assert.Single(viewModel.Screens);
        using var watch = new CollectionWatch(
            viewModel.Connections,
            viewModel.Screens,
            viewModel.Workspaces,
            viewModel.ConnectionsPreview,
            viewModel.ScreensPreview);

        viewModel.RefreshCatalog(Snapshot("Prod web"));
        viewModel.RefreshCatalog(Snapshot("Prod web"));

        Assert.Equal(0, watch.Count);
        Assert.Same(connection, Assert.Single(viewModel.Connections));
        Assert.Same(screen, Assert.Single(viewModel.Screens));
    }

    [Fact]
    public void A_renamed_connection_still_reaches_the_lists()
    {
        using var viewModel = CreateViewModel(new FixedCatalog(Snapshot("Prod web")));
        using var watch = new CollectionWatch(viewModel.Connections);

        viewModel.RefreshCatalog(Snapshot("Staging web"));

        Assert.True(watch.Count > 0);
        Assert.Equal("Staging web", Assert.Single(viewModel.Connections).Name);
        Assert.Equal(
            "Staging web",
            Assert.Single(viewModel.ConnectionsPreview).Name);
    }

    [Fact]
    public void An_isolation_change_reaches_the_workspace_list()
    {
        using var viewModel = CreateViewModel(new FixedCatalog(Snapshot("Prod web")));
        var original = Assert.Single(viewModel.Workspaces);
        using var watch = new CollectionWatch(viewModel.Workspaces);

        var isolatedSnapshot = Snapshot("Prod web", isIsolated: true);
        Assert.True(Assert.Single(isolatedSnapshot.Workspaces).Value.IsIsolated);
        viewModel.RefreshCatalog(isolatedSnapshot);

        Assert.True(watch.Count > 0);
        var isolated = Assert.Single(viewModel.Workspaces);
        Assert.NotSame(original, isolated);
        Assert.True(isolated.IsIsolated);
    }

    private static MainWindowViewModel CreateViewModel(IDefinitionCatalog catalog)
    {
        var files = new EmptyFileClients();
        return new MainWindowViewModel(
            DispatchProxy.Create<ISessionHostClient, RejectingSessionHostProxy>(),
            catalog,
            new UnusedConnectionRuntime(),
            new EmptySecretVault(),
            files,
            files,
            new TerminalStartupCommandDispatcher(
                new SuccessfulAuditStore(),
                TimeProvider.System));
    }

    public class RejectingSessionHostProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private static DefinitionCatalogSnapshot Snapshot(
        string connectionName,
        bool isIsolated = false)
    {
        var connection = new ConnectionProfile(
            new ConnectionId("prod-web"),
            ConnectionProfile.CurrentSchemaVersion,
            connectionName,
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var layout = new LayoutDefinition(
            new LayoutId("single"),
            LayoutDefinition.CurrentSchemaVersion,
            "Single",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var screen = new ScreenDefinition(
            new ScreenId("deploy"),
            ScreenDefinition.CurrentSchemaVersion,
            "Deploy",
            null,
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("main-panel"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    "Main",
                    connection.Id,
                    PanelStartupBehavior.None),
            ]);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("release"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Release",
            null,
            null,
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("release-connection"),
                    connection.Id),
            ],
            isIsolated: isIsolated);

        return DefinitionCatalogSnapshot.Empty with
        {
            Connections = [Store(connection)],
            Layouts = [Store(layout)],
            Screens = [Store(screen)],
            Workspaces = [Store(workspace)],
        };
    }

    private static StoredDefinition<T> Store<T>(T definition)
        where T : IDurableDefinition =>
        new(definition, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    /// <summary>Counts every structural change across a set of collections.</summary>
    private sealed class CollectionWatch : IDisposable
    {
        private readonly IReadOnlyList<INotifyCollectionChanged> _sources;

        public CollectionWatch(params INotifyCollectionChanged[] sources)
        {
            _sources = sources;
            foreach (var source in sources)
            {
                source.CollectionChanged += OnChanged;
            }
        }

        public int Count { get; private set; }

        public void Dispose()
        {
            foreach (var source in _sources)
            {
                source.CollectionChanged -= OnChanged;
            }
        }

        private void OnChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            _ = sender;
            _ = args;
            Count++;
        }
    }

    private sealed class FixedCatalog(DefinitionCatalogSnapshot snapshot)
        : IDefinitionCatalog
    {
        public DefinitionCatalogSnapshot Snapshot { get; } = snapshot;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
            ConnectionProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutAsync(
            LayoutDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveScreenAsync(
            ScreenDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
            WorkspaceDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
            ThemePreference definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
            TerminalProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
            KeymapProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> SaveFileProviderProfileAsync(
            FileProviderProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> SaveAiProviderProfileAsync(
            AiProviderProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>> SaveMcpServerProfileAsync(
            McpServerProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
            QuickTerminalSettings definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
            DefinitionKey key,
            long expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedConnectionRuntime : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptySecretVault : ISecretVault
    {
        public SecretVaultAvailability Availability { get; } = new(
            SecretVaultAvailabilityState.Available,
            SecretVaultPersistenceKind.MemoryOnly,
            SecretVaultCapabilities.ListMetadata,
            "test",
            "test_vault",
            "Test vault");

        public void Dispose()
        {
        }

        public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
            ListSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([]));

        public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
            CreateSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
            ReplaceSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
            RelabelSecretRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
            DeleteSecretRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
            GetSecretMetadataRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyFileClients : IFilePanelClient, IFileTransferQueueClient
    {
        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; } = [];

        public IReadOnlyList<FilePanelTransferSnapshot> Transfers { get; } = [];

        public event EventHandler? TransfersChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
            FilePanelTransferRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<Unit>> CancelAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SuccessfulAuditStore : IAuditStore
    {
        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AuditStoreResult<Unit>.Success(Unit.Value));

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success([]));
    }
}
