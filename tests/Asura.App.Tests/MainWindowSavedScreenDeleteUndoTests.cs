using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class MainWindowSavedScreenDeleteUndoTests
{
    [Fact]
    public void Undo_surface_notifies_pending_status_and_availability()
    {
        var screen = Screen("screen-alpha", "Alpha");
        var surface = new SavedScreenDeleteUndoViewModel(
            new SavedScreenCatalog([Store(screen, 7)]));
        var changed = new List<string>();
        surface.PropertyChanged += (_, args) =>
            changed.Add(Assert.IsType<string>(args.PropertyName));

        surface.Publish(Store(screen, 7));

        var receipt = Assert.IsType<SavedScreenDeleteUndoReceipt>(
            surface.Pending);
        Assert.Equal(screen.Id, receipt.ScreenId);
        Assert.True(surface.HasPending);
        Assert.True(surface.CanUndo);
        Assert.False(surface.IsRestoring);
        Assert.Contains("Running instances were not changed", surface.Status);
        Assert.Contains(nameof(surface.Pending), changed, StringComparer.Ordinal);
        Assert.Contains(nameof(surface.HasPending), changed, StringComparer.Ordinal);
        Assert.Contains(nameof(surface.CanUndo), changed, StringComparer.Ordinal);
        Assert.Contains(nameof(surface.Status), changed, StringComparer.Ordinal);

        changed.Clear();
        surface.Dismiss();

        Assert.Null(surface.Pending);
        Assert.False(surface.HasPending);
        Assert.False(surface.CanUndo);
        Assert.Equal("Saved-screen delete undo dismissed.", surface.Status);
        Assert.Contains(nameof(surface.Pending), changed, StringComparer.Ordinal);
        Assert.Contains(nameof(surface.HasPending), changed, StringComparer.Ordinal);
        Assert.Contains(nameof(surface.CanUndo), changed, StringComparer.Ordinal);
        Assert.Contains(nameof(surface.Status), changed, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Create_screen_remains_a_draft_until_the_editor_request_is_saved()
    {
        var layout = new LayoutDefinition(
            new LayoutId("layout-authoring"),
            LayoutDefinition.CurrentSchemaVersion,
            "Columns",
            new LayoutGrid(2, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("left"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
                new LayoutSlotDefinition(
                    new LayoutSlotId("right"),
                    new LayoutGridBounds(1, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var connection = new ConnectionProfile(
            new ConnectionId("local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Local",
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var catalog = new SavedScreenCatalog([]);
        catalog.SetAuthoringDefinitions(layout, connection);
        using var viewModel = CreateViewModel(catalog);

        using var editor = viewModel.CreateNewSavedScreenEditor("Operations");

        Assert.True(editor.IsNew);
        Assert.Empty(catalog.Snapshot.Screens);
        Assert.Null(catalog.LastSavedScreen);

        editor.Panels[1].Kind = ScreenPanelKind.FileViewer;
        var request = editor.CreateSaveRequest();

        Assert.Empty(catalog.Snapshot.Screens);
        Assert.Null(catalog.LastSavedScreen);

        var saved = await viewModel.SaveSavedScreenAsync(
            request,
            CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Null(catalog.LastExpectedScreenRevision);
        Assert.Same(request.Definition, catalog.LastSavedScreen);
        Assert.Same(request.Definition, Assert.Single(catalog.Snapshot.Screens).Value);
    }

    [Fact]
    public async Task Undo_restores_the_exact_deleted_definition_as_a_create()
    {
        var screen = Screen("screen-alpha", "Alpha");
        var catalog = new SavedScreenCatalog([Store(screen, 7)]);
        using var viewModel = CreateViewModel(catalog);

        var deleted = await viewModel.DeleteSavedScreenAsync(
            screen.Key,
            revision: 7,
            CancellationToken.None);

        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        var receipt = Assert.IsType<SavedScreenDeleteUndoReceipt>(
            viewModel.SavedScreenDeleteUndo.Pending);
        Assert.Equal(screen.Id, receipt.ScreenId);
        Assert.Equal(screen.Name, receipt.ScreenName);
        Assert.True(viewModel.SavedScreenDeleteUndo.CanUndo);
        Assert.Contains(
            "Running instances were not changed",
            viewModel.SavedScreenDeleteUndo.Status,
            StringComparison.Ordinal);

        var restored = await viewModel.UndoSavedScreenDeleteAsync(CancellationToken.None);

        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Same(screen, catalog.LastSavedScreen);
        Assert.Null(catalog.LastExpectedScreenRevision);
        Assert.Same(screen, restored.Value!.Value);
        Assert.Same(screen, Assert.Single(catalog.Snapshot.Screens).Value);
        Assert.Null(viewModel.SavedScreenDeleteUndo.Pending);
        Assert.False(viewModel.SavedScreenDeleteUndo.HasPending);
        Assert.False(viewModel.SavedScreenDeleteUndo.CanUndo);
        Assert.Equal("Restored “Alpha”.", viewModel.SavedScreenDeleteUndo.Status);
    }

    [Fact]
    public async Task Stale_failed_and_cancelled_deletes_do_not_publish_or_replace_a_receipt()
    {
        var alpha = Screen("screen-alpha", "Alpha");
        var beta = Screen("screen-beta", "Beta");
        var catalog = new SavedScreenCatalog(
        [
            Store(alpha, 3),
            Store(beta, 5),
        ]);
        using var viewModel = CreateViewModel(catalog);

        var stale = await viewModel.DeleteSavedScreenAsync(
            beta.Key,
            revision: 4,
            CancellationToken.None);

        Assert.False(stale.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, stale.Error!.Code);
        Assert.Equal(5, stale.Error.CurrentRevision);
        Assert.Equal(0, catalog.DeleteAttempts);
        Assert.Null(viewModel.SavedScreenDeleteUndo.Pending);

        Assert.True((await viewModel.DeleteSavedScreenAsync(
            alpha.Key,
            revision: 3,
            CancellationToken.None)).IsSuccess);
        var alphaReceipt = Assert.IsType<SavedScreenDeleteUndoReceipt>(
            viewModel.SavedScreenDeleteUndo.Pending);

        catalog.NextDeleteError = new DefinitionStoreError(
            DefinitionStoreErrorCode.StorageFailure,
            "Delete failed.");
        var failed = await viewModel.DeleteSavedScreenAsync(
            beta.Key,
            revision: 5,
            CancellationToken.None);

        Assert.False(failed.IsSuccess);
        Assert.Same(alphaReceipt, viewModel.SavedScreenDeleteUndo.Pending);

        catalog.NextDeleteError = new DefinitionStoreError(
            DefinitionStoreErrorCode.Cancelled,
            "Delete cancelled.");
        var cancelled = await viewModel.DeleteSavedScreenAsync(
            beta.Key,
            revision: 5,
            CancellationToken.None);

        Assert.False(cancelled.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.Cancelled, cancelled.Error!.Code);
        Assert.Same(alphaReceipt, viewModel.SavedScreenDeleteUndo.Pending);
    }

    [Fact]
    public async Task Recreated_identity_conflicts_without_overwrite_and_keeps_undo_retryable()
    {
        var deletedScreen = Screen("screen-alpha", "Alpha");
        var catalog = new SavedScreenCatalog([Store(deletedScreen, 9)]);
        using var viewModel = CreateViewModel(catalog);
        Assert.True((await viewModel.DeleteSavedScreenAsync(
            deletedScreen.Key,
            revision: 9,
            CancellationToken.None)).IsSuccess);
        var receipt = viewModel.SavedScreenDeleteUndo.Pending;
        var replacement = Screen("screen-alpha", "Replacement");
        catalog.Add(replacement, revision: 12);

        var restored = await viewModel.UndoSavedScreenDeleteAsync(CancellationToken.None);

        Assert.False(restored.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, restored.Error!.Code);
        Assert.Same(deletedScreen, catalog.LastSavedScreen);
        Assert.Null(catalog.LastExpectedScreenRevision);
        Assert.Same(replacement, Assert.Single(catalog.Snapshot.Screens).Value);
        Assert.Same(receipt, viewModel.SavedScreenDeleteUndo.Pending);
        Assert.True(viewModel.SavedScreenDeleteUndo.CanUndo);
        Assert.Contains(
            "Retry or dismiss",
            viewModel.SavedScreenDeleteUndo.Status,
            StringComparison.Ordinal);

        viewModel.DismissSavedScreenDeleteUndo();

        Assert.Null(viewModel.SavedScreenDeleteUndo.Pending);
        Assert.False(viewModel.SavedScreenDeleteUndo.CanUndo);
        Assert.Equal(
            "Saved-screen delete undo dismissed.",
            viewModel.SavedScreenDeleteUndo.Status);
    }

    [Fact]
    public async Task Cancellation_retains_the_previous_receipt_and_restores_undo_availability()
    {
        var alpha = Screen("screen-alpha", "Alpha");
        var beta = Screen("screen-beta", "Beta");
        var catalog = new SavedScreenCatalog(
        [
            Store(alpha, 2),
            Store(beta, 4),
        ]);
        using var viewModel = CreateViewModel(catalog);
        Assert.True((await viewModel.DeleteSavedScreenAsync(
            alpha.Key,
            revision: 2,
            CancellationToken.None)).IsSuccess);
        var receipt = viewModel.SavedScreenDeleteUndo.Pending;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.DeleteSavedScreenAsync(
                    beta.Key,
                    revision: 4,
                    cancellation.Token)
                .AsTask());

        Assert.Same(receipt, viewModel.SavedScreenDeleteUndo.Pending);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.UndoSavedScreenDeleteAsync(cancellation.Token).AsTask());

        Assert.Same(receipt, viewModel.SavedScreenDeleteUndo.Pending);
        Assert.False(viewModel.SavedScreenDeleteUndo.IsRestoring);
        Assert.True(viewModel.SavedScreenDeleteUndo.CanUndo);
        Assert.Contains(
            "Restore cancelled",
            viewModel.SavedScreenDeleteUndo.Status,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_delete_replaces_the_one_level_receipt()
    {
        var alpha = Screen("screen-alpha", "Alpha");
        var beta = Screen("screen-beta", "Beta");
        var catalog = new SavedScreenCatalog(
        [
            Store(alpha, 1),
            Store(beta, 6),
        ]);
        using var viewModel = CreateViewModel(catalog);

        Assert.True((await viewModel.DeleteSavedScreenAsync(
            alpha.Key,
            revision: 1,
            CancellationToken.None)).IsSuccess);
        var first = viewModel.SavedScreenDeleteUndo.Pending;
        Assert.True((await viewModel.DeleteSavedScreenAsync(
            beta.Key,
            revision: 6,
            CancellationToken.None)).IsSuccess);

        var second = Assert.IsType<SavedScreenDeleteUndoReceipt>(
            viewModel.SavedScreenDeleteUndo.Pending);
        Assert.NotSame(first, second);
        Assert.Equal(beta.Id, second.ScreenId);
        Assert.Equal(beta.Name, second.ScreenName);
        Assert.Contains("Beta", viewModel.SavedScreenDeleteUndo.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_in_flight_disables_and_rejects_reentry()
    {
        var screen = Screen("screen-alpha", "Alpha");
        var catalog = new SavedScreenCatalog([Store(screen, 8)]);
        using var viewModel = CreateViewModel(catalog);
        Assert.True((await viewModel.DeleteSavedScreenAsync(
            screen.Key,
            revision: 8,
            CancellationToken.None)).IsSuccess);
        catalog.PauseNextSave();

        var restore = viewModel.UndoSavedScreenDeleteAsync(CancellationToken.None).AsTask();
        await catalog.SaveStarted.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(viewModel.SavedScreenDeleteUndo.IsRestoring);
            Assert.False(viewModel.SavedScreenDeleteUndo.CanUndo);

            var duplicate = await viewModel.UndoSavedScreenDeleteAsync(CancellationToken.None);

            Assert.False(duplicate.IsSuccess);
            Assert.Contains("already being restored", duplicate.Error!.Message);
            var receipt = viewModel.SavedScreenDeleteUndo.Pending;
            viewModel.DismissSavedScreenDeleteUndo();
            Assert.Same(receipt, viewModel.SavedScreenDeleteUndo.Pending);
            Assert.NotNull(viewModel.SavedScreenDeleteUndo.Pending);
        }
        finally
        {
            catalog.ResumeSave();
        }

        Assert.True((await restore).IsSuccess);
        Assert.False(viewModel.SavedScreenDeleteUndo.IsRestoring);
        Assert.Null(viewModel.SavedScreenDeleteUndo.Pending);
    }

    private static MainWindowViewModel CreateViewModel(IDefinitionCatalog catalog) =>
        new(
            DispatchProxy.Create<ISessionHostClient, EmptyDependencyProxy>(),
            catalog,
            DispatchProxy.Create<IConnectionRuntime, EmptyDependencyProxy>(),
            DispatchProxy.Create<ISecretVault, EmptyDependencyProxy>(),
            DispatchProxy.Create<IFilePanelClient, EmptyDependencyProxy>(),
            DispatchProxy.Create<IFileTransferQueueClient, EmptyDependencyProxy>(),
            new TerminalStartupCommandDispatcher(
                DispatchProxy.Create<IAuditStore, EmptyDependencyProxy>(),
                TimeProvider.System));

    private static ScreenDefinition Screen(string id, string name) => new(
        new ScreenId(id),
        ScreenDefinition.CurrentSchemaVersion,
        name,
        "Private saved-screen description",
        new LayoutId("layout-main"),
        [
            new ScreenPanelDefinition(
                new ScreenPanelId("terminal"),
                new LayoutSlotId("main"),
                ScreenPanelKind.Terminal,
                "Production shell",
                ConnectionId: null,
                new PanelStartupBehavior("/srv/private", ["deploy --production"])),
        ],
        ["operations", "private"],
        AgentPolicy.Default);

    private static StoredDefinition<ScreenDefinition> Store(
        ScreenDefinition screen,
        long revision) =>
        new(
            screen,
            revision,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(revision));

    public class EmptyDependencyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            return targetMethod?.Name switch
            {
                "get_Availability" => new SecretVaultAvailability(
                    SecretVaultAvailabilityState.Available,
                    SecretVaultPersistenceKind.MemoryOnly,
                    SecretVaultCapabilities.ListMetadata,
                    "test",
                    "test_vault",
                    "Test vault"),
                "ListMetadataAsync" => ValueTask.FromResult(
                    SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([])),
                "get_Profiles" => Array.Empty<FileProviderProfileDescriptor>(),
                "get_Transfers" => Array.Empty<FilePanelTransferSnapshot>(),
                "add_TransfersChanged" or "remove_TransfersChanged" or "Dispose" => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
        }
    }

    private sealed class SavedScreenCatalog(
        IReadOnlyList<StoredDefinition<ScreenDefinition>> screens)
        : IDefinitionCatalog
    {
        private TaskCompletionSource? _saveStarted;
        private TaskCompletionSource? _resumeSave;
        private Task _saveStartedTask = Task.CompletedTask;

        public DefinitionCatalogSnapshot Snapshot { get; private set; } =
            DefinitionCatalogSnapshot.Empty with { Screens = screens };

        public int DeleteAttempts { get; private set; }

        public ScreenDefinition? LastSavedScreen { get; private set; }

        public long? LastExpectedScreenRevision { get; private set; }

        public DefinitionStoreError? NextDeleteError { get; set; }

        public Task SaveStarted => _saveStartedTask;

        public event EventHandler? Changed;

        public void SetAuthoringDefinitions(
            LayoutDefinition layout,
            ConnectionProfile connection)
        {
            Snapshot = Snapshot with
            {
                Layouts =
                [
                    new StoredDefinition<LayoutDefinition>(
                        layout,
                        1,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch),
                ],
                Connections =
                [
                    new StoredDefinition<ConnectionProfile>(
                        connection,
                        1,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch),
                ],
            };
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Add(ScreenDefinition screen, long revision)
        {
            Snapshot = Snapshot with
            {
                Screens =
                [
                    .. Snapshot.Screens
                                        .Where(item => item.Value.Id != screen.Id)
,
                    Store(screen, revision),
                ],
            };
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void PauseNextSave()
        {
            _saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumeSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _saveStartedTask = _saveStarted.Task;
        }

        public void ResumeSave() => _resumeSave?.TrySetResult();

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

        public async ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>>
            SaveScreenAsync(
                ScreenDefinition definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSavedScreen = definition;
            LastExpectedScreenRevision = expectedRevision;
            var saveStarted = _saveStarted;
            var resumeSave = _resumeSave;
            _saveStarted = null;
            saveStarted?.TrySetResult();
            if (resumeSave is not null)
            {
                await resumeSave.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var current = Snapshot.Screens
                .SingleOrDefault(item => item.Value.Id == definition.Id);
            if (expectedRevision is null && current is not null)
            {
                return DefinitionStoreResult<StoredDefinition<ScreenDefinition>>.Failure(
                    new DefinitionStoreError(
                        DefinitionStoreErrorCode.RevisionConflict,
                        "That saved-screen identity already exists.",
                        current.Revision));
            }

            var stored = Store(definition, current?.Revision + 1 ?? 1);
            Snapshot = Snapshot with
            {
                Screens =
                [
                    .. Snapshot.Screens
                                        .Where(item => item.Value.Id != definition.Id)
,
                    stored,
                ],
            };
            Changed?.Invoke(this, EventArgs.Empty);
            return DefinitionStoreResult<StoredDefinition<ScreenDefinition>>.Success(stored);
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
            WorkspaceDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
            ThemePreference definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>>
            SaveTerminalProfileAsync(
                TerminalProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
            KeymapProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>>
            SaveFileProviderProfileAsync(
                FileProviderProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>>
            SaveAiProviderProfileAsync(
                AiProviderProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>>
            SaveMcpServerProfileAsync(
                McpServerProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>>
            SaveQuickTerminalSettingsAsync(
                QuickTerminalSettings definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
            DefinitionKey key,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            DeleteAttempts++;
            cancellationToken.ThrowIfCancellationRequested();
            if (NextDeleteError is { } error)
            {
                NextDeleteError = null;
                return ValueTask.FromResult(
                    DefinitionStoreResult<Unit>.Failure(error));
            }

            var current = Snapshot.Screens.SingleOrDefault(item => item.Value.Key == key);
            if (current is null)
            {
                return ValueTask.FromResult(DefinitionStoreResult<Unit>.Failure(
                    new DefinitionStoreError(
                        DefinitionStoreErrorCode.NotFound,
                        "That saved screen no longer exists.")));
            }

            if (current.Revision != expectedRevision)
            {
                return ValueTask.FromResult(DefinitionStoreResult<Unit>.Failure(
                    new DefinitionStoreError(
                        DefinitionStoreErrorCode.RevisionConflict,
                        "That saved screen changed.",
                        current.Revision)));
            }

            Snapshot = Snapshot with
            {
                Screens = [.. Snapshot.Screens.Where(item => item.Value.Key != key)],
            };
            Changed?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromResult(DefinitionStoreResult<Unit>.Success(Unit.Value));
        }
    }
}
