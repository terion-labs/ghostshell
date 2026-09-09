using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.Tests;

public sealed class MainWindowKeybindingEditorTests
{
    [Fact]
    public void Opening_keybinding_settings_selects_the_tmux_preset_and_clone_is_editable()
    {
        var catalog = new RecordingDefinitionCatalog(KeymapSnapshot());
        using var viewModel = CreateViewModel(catalog);

        viewModel.ShowSettings(SettingsPage.Keybindings);

        Assert.Equal(ShellRoute.Settings, viewModel.Route);
        Assert.Equal(BuiltInKeymaps.TmuxApplicationId, viewModel.SelectedKeybindingProfile?.Id);
        Assert.True(viewModel.CanCloneSelectedKeybindingProfile);
        Assert.True(viewModel.KeybindingEditorSession?.IsReadOnly);

        viewModel.CloneSelectedKeybindingProfile();

        var clone = Assert.IsType<KeybindingProfileItemViewModel>(
            viewModel.SelectedKeybindingProfile);
        var session = Assert.IsType<KeybindingEditorSessionViewModel>(
            viewModel.KeybindingEditorSession);
        Assert.StartsWith("user.keymap.", clone.Id.Value, StringComparison.Ordinal);
        Assert.Equal("tmux-like application copy", clone.Name);
        Assert.True(clone.IsUnsaved);
        Assert.False(clone.IsBuiltIn);
        Assert.Null(clone.Revision);
        Assert.False(session.IsReadOnly);
        Assert.Equal(clone.Id, session.ProfileId);
        Assert.Equal(BuiltInKeymaps.TmuxApplicationId, session.Editor.BasedOn);
        Assert.Contains(viewModel.KeybindingProfiles, item => item.Id == clone.Id);
        Assert.False(viewModel.HasOperationError);
    }

    [Fact]
    public async Task Edited_clone_saves_as_a_custom_profile_and_reopens_at_its_revision()
    {
        var catalog = new RecordingDefinitionCatalog(KeymapSnapshot());
        using var viewModel = CreateViewModel(catalog);
        viewModel.ShowSettings(SettingsPage.Keybindings);
        viewModel.CloneSelectedKeybindingProfile();
        var draftSession = Assert.IsType<KeybindingEditorSessionViewModel>(
            viewModel.KeybindingEditorSession);
        var newTab = Assert.Single(
            draftSession.Rows,
            row => row.Row.CommandId == BuiltInCommands.NewTab);
        draftSession.RecordShortcut(
            newTab.Id,
            [new KeyStroke("T", KeyModifiers.Control | KeyModifiers.Shift)]);

        var result = await viewModel.SaveKeybindingEditorAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.IsType<StoredDefinition<KeymapProfile>>(result.Value);
        Assert.Null(catalog.LastExpectedRevision);
        Assert.Equal(saved.Value.Id, catalog.LastSavedKeymap?.Id);
        Assert.Equal(BuiltInKeymaps.TmuxApplicationId, saved.Value.BasedOn);
        Assert.Equal(
            KeySequence.Of(new KeyStroke("T", KeyModifiers.Control | KeyModifiers.Shift)),
            Assert.Single(
                saved.Value.Bindings,
                binding => binding.CommandId == BuiltInCommands.NewTab).Sequence);

        var selected = Assert.IsType<KeybindingProfileItemViewModel>(
            viewModel.SelectedKeybindingProfile);
        var savedSession = Assert.IsType<KeybindingEditorSessionViewModel>(
            viewModel.KeybindingEditorSession);
        Assert.Equal(saved.Value.Id, selected.Id);
        Assert.Equal(saved.Revision, selected.Revision);
        Assert.False(selected.IsUnsaved);
        Assert.False(selected.IsBuiltIn);
        Assert.Equal(saved.Revision, savedSession.Editor.ExpectedRevision);
        Assert.False(savedSession.IsDirty);
        Assert.Equal(saved.Value.Id, viewModel.ActiveApplicationKeymap.Id);
        Assert.Equal(saved.Revision, viewModel.ActiveApplicationKeymapRevision);
        Assert.Equal(
            KeySequence.Of(new KeyStroke("T", KeyModifiers.Control | KeyModifiers.Shift)).ToString(),
            Assert.Single(
                viewModel.LauncherSearchResults,
                item => item.Target is LauncherSearchTarget.Command command
                    && command.Id == BuiltInCommands.NewTab)
                .TrailingText);
        Assert.False(viewModel.HasOperationError);

        using var restarted = CreateViewModel(catalog);
        Assert.Equal(saved.Value.Id, restarted.ActiveApplicationKeymap.Id);
        Assert.Equal(saved.Revision, restarted.ActiveApplicationKeymapRevision);
        var runtimeResolver = new ApplicationKeySequenceResolver(
            restarted.ActiveApplicationKeymap);
        var runtimeMatch = runtimeResolver.Resolve(
            new KeyStroke("T", KeyModifiers.Control | KeyModifiers.Shift),
            CommandContext.Workspace,
            DateTimeOffset.UnixEpoch);
        Assert.Equal(ApplicationKeyResolutionKind.Matched, runtimeMatch.Kind);
        Assert.Equal(BuiltInCommands.NewTab, runtimeMatch.Binding?.CommandId);
    }

    [Fact]
    public async Task Built_in_preset_cannot_be_saved_in_place()
    {
        var catalog = new RecordingDefinitionCatalog(KeymapSnapshot());
        using var viewModel = CreateViewModel(catalog);
        viewModel.ShowSettings(SettingsPage.Keybindings);

        var result = await viewModel.SaveKeybindingEditorAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(catalog.LastSavedKeymap);
        Assert.True(viewModel.HasOperationError);
        Assert.Contains("clone", viewModel.OperationError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Definition_bundle_status_is_trimmed_and_notifies_only_when_it_changes()
    {
        var catalog = new RecordingDefinitionCatalog(KeymapSnapshot());
        using var viewModel = CreateViewModel(catalog);
        var notifications = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (string.Equals(eventArgs.PropertyName, nameof(MainWindowViewModel.DefinitionBundleStatus), StringComparison.Ordinal))
            {
                notifications++;
            }
        };

        viewModel.SetDefinitionBundleStatus("  Imported 3 definitions.  ");
        viewModel.SetDefinitionBundleStatus("Imported 3 definitions.");

        Assert.Equal("Imported 3 definitions.", viewModel.DefinitionBundleStatus);
        Assert.Equal(1, notifications);
        Assert.Throws<ArgumentException>(() => viewModel.SetDefinitionBundleStatus("   "));
    }

    [Fact]
    public void Launcher_search_projects_connection_screen_and_workspace_catalog_targets()
    {
        var connection = new ConnectionProfile(
            new ConnectionId("production-connection"),
            ConnectionProfile.CurrentSchemaVersion,
            "Production connection",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var layout = new LayoutDefinition(
            new LayoutId("production-layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Production layout",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var screen = new ScreenDefinition(
            new ScreenId("production-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Production screen",
            null,
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("production-panel"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    "Production terminal",
                    connection.Id,
                    PanelStartupBehavior.None),
            ]);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("production-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Production workspace",
            null,
            null,
            [
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("production-entry"),
                    screen.Id,
                    "Production screen"),
            ]);
        var catalog = new RecordingDefinitionCatalog(
            KeymapSnapshot() with
            {
                Connections = [Store(connection, 1)],
                Layouts = [Store(layout, 2)],
                Screens = [Store(screen, 3)],
                Workspaces = [Store(workspace, 4)],
            });
        using var viewModel = CreateViewModel(catalog);

        viewModel.LauncherSearchQuery = "production";

        Assert.Contains(
            viewModel.LauncherSearchResults,
            item => item.Target == new LauncherSearchTarget.Connection(connection.Id));
        Assert.Contains(
            viewModel.LauncherSearchResults,
            item => item.Target == new LauncherSearchTarget.Screen(screen.Id));
        Assert.Contains(
            viewModel.LauncherSearchResults,
            item => item.Target == new LauncherSearchTarget.Workspace(workspace.Id));
        Assert.DoesNotContain(
            viewModel.LauncherSearchResults,
            item => item.Target is LauncherSearchTarget.Command);
    }

    [Fact]
    public void Launcher_search_preserves_parameterized_command_invocations()
    {
        var catalog = new RecordingDefinitionCatalog(KeymapSnapshot());
        using var viewModel = CreateViewModel(catalog);

        viewModel.LauncherSearchQuery = "focus panel";

        var focusCommands = viewModel.LauncherSearchResults
            .Where(item => item.Target is LauncherSearchTarget.Command command
                && command.Id == BuiltInCommands.FocusPanel)
            .ToArray();
        Assert.Equal(5, focusCommands.Length);
        var right = Assert.Single(
            focusCommands,
            item => item.Target is LauncherSearchTarget.Command command
                && command.Arguments.TryGetValue("direction", out var direction)
                && string.Equals(direction, "right", StringComparison.Ordinal));
        Assert.Contains("right", right.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("direction=right", right.Detail, StringComparison.Ordinal);
        Assert.NotEqual("Unbound", right.TrailingText, StringComparer.Ordinal);
    }

    [Fact]
    public void Launcher_search_exposes_create_types_and_keeps_uninstalled_types_disabled()
    {
        var catalog = new RecordingDefinitionCatalog(KeymapSnapshot());
        using var viewModel = CreateViewModel(catalog);

        viewModel.LauncherSearchQuery = "terminal";

        var terminal = Assert.Single(
            viewModel.LauncherSearchResults,
            item => item.Target == new LauncherSearchTarget.CreatePanel(PanelKind.Terminal));
        Assert.True(terminal.IsAvailable);

        viewModel.LauncherSearchQuery = "browser";

        var browser = Assert.Single(
            viewModel.LauncherSearchResults,
            item => item.Target == new LauncherSearchTarget.CreatePanel(PanelKind.Browser));
        Assert.False(browser.IsAvailable);
        Assert.Contains(
            "embedded browser is unavailable",
            browser.DisplayDetail,
            StringComparison.OrdinalIgnoreCase);

        viewModel.LauncherSearchQuery = "new tab";

        Assert.True(Assert.Single(
            viewModel.LauncherSearchResults,
            item => item.Target is LauncherSearchTarget.Command command
                && command.Id == BuiltInCommands.NewTab).IsAvailable);
    }

    [Fact]
    public void Launcher_search_exposes_both_tab_move_commands_with_their_shortcuts()
    {
        var catalog = new RecordingDefinitionCatalog(KeymapSnapshot());
        using var viewModel = CreateViewModel(catalog);

        viewModel.LauncherSearchQuery = "move tab";

        var commands = viewModel.LauncherSearchResults
            .Where(item => item.Target is LauncherSearchTarget.Command command
                && (command.Id == BuiltInCommands.MoveTabLeft
                    || command.Id == BuiltInCommands.MoveTabRight))
            .ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Contains(commands, item => string.Equals(item.Title, "Move tab left", StringComparison.Ordinal));
        Assert.Contains(commands, item => string.Equals(item.Title, "Move tab right", StringComparison.Ordinal));
        Assert.All(commands, item =>
        {
            Assert.NotEqual("Unbound", item.TrailingText, StringComparer.Ordinal);
            Assert.False(item.IsAvailable);
            Assert.Equal("Unavailable in the current route.", item.UnavailableReason);
        });
    }

    [Fact]
    public async Task Workspace_editor_saves_the_complete_definition_and_closes()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("workspace-test"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Original",
            "Description",
            "#C97B2A",
            [],
            icon: "terminal");
        var catalog = new RecordingDefinitionCatalog(
            KeymapSnapshot() with { Workspaces = [Store(workspace, 7)] });
        using var viewModel = CreateViewModel(catalog);

        Assert.Equal(Symbol.WindowConsole, Assert.Single(viewModel.Workspaces).IconSymbol);

        viewModel.BeginEditWorkspace(workspace.Id);
        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.WorkspaceEditor);
        editor.Name = "Operations";
        editor.Icon = "server";

        var result = await viewModel.SaveWorkspaceEditorAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, catalog.LastExpectedWorkspaceRevision);
        Assert.Equal("Operations", catalog.LastSavedWorkspace?.Name);
        Assert.Equal("server", catalog.LastSavedWorkspace?.Icon);
        Assert.Null(viewModel.WorkspaceEditor);
        Assert.Equal(ShellOverlay.None, viewModel.Overlay);
        Assert.False(viewModel.HasOperationError);

        using var reopened = CreateViewModel(catalog);
        Assert.Equal(Symbol.Server, Assert.Single(reopened.Workspaces).IconSymbol);
    }

    [Fact]
    public async Task Definition_edit_facade_forwards_draft_notifications_and_save_result()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("workspace-definition-edit"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Original",
            "Description",
            "#C97B2A",
            []);
        var catalog = new RecordingDefinitionCatalog(
            KeymapSnapshot() with { Workspaces = [Store(workspace, 7)] });
        using var viewModel = CreateViewModel(catalog);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);

        viewModel.BeginEditWorkspace(workspace.Id);
        viewModel.EditorName = "Operations";
        viewModel.EditorDescription = "Updated description";
        var result = await viewModel.SaveDefinitionEditAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, catalog.LastExpectedWorkspaceRevision);
        Assert.Equal("Operations", catalog.LastSavedWorkspace?.Name);
        Assert.Equal("Updated description", catalog.LastSavedWorkspace?.Description);
        Assert.Equal(ShellOverlay.None, viewModel.Overlay);
        Assert.Contains(nameof(MainWindowViewModel.EditorName), changed, StringComparer.Ordinal);
        Assert.Contains(
            nameof(MainWindowViewModel.EditorDescription),
            changed,
            StringComparer.Ordinal);
    }

    [Fact]
    public void Dirty_workspace_editor_blocks_unconfirmed_navigation()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("workspace-test"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Original",
            null,
            null,
            []);
        var catalog = new RecordingDefinitionCatalog(
            KeymapSnapshot() with { Workspaces = [Store(workspace, 3)] });
        using var viewModel = CreateViewModel(catalog);
        viewModel.BeginEditWorkspace(workspace.Id);
        viewModel.WorkspaceEditor!.Name = "Changed";

        viewModel.ShowSettings(SettingsPage.Workspaces);

        Assert.Equal(ShellRoute.Workspace, viewModel.Route);
        Assert.Equal(ShellOverlay.DefinitionEditor, viewModel.Overlay);
        Assert.NotNull(viewModel.WorkspaceEditor);
        Assert.Contains("discard", viewModel.OperationError, StringComparison.OrdinalIgnoreCase);
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
            new TerminalStartupCommandDispatcher(new SuccessfulAuditStore(), TimeProvider.System));
    }

    private static DefinitionCatalogSnapshot KeymapSnapshot() =>
        DefinitionCatalogSnapshot.Empty with
        {
            Keymaps = [.. BuiltInKeymaps.All.Select((profile, index) => Store(profile, index + 1))],
        };

    private static StoredDefinition<T> Store<T>(T value, long revision)
        where T : IDurableDefinition =>
        new(value, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    public class RejectingSessionHostProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class RecordingDefinitionCatalog(DefinitionCatalogSnapshot snapshot)
        : IDefinitionCatalog
    {
        public DefinitionCatalogSnapshot Snapshot { get; private set; } = snapshot;

        public KeymapProfile? LastSavedKeymap { get; private set; }

        public long? LastExpectedRevision { get; private set; }

        public WorkspaceDefinition? LastSavedWorkspace { get; private set; }

        public long? LastExpectedWorkspaceRevision { get; private set; }

        public event EventHandler? Changed;

        public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
            KeymapProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSavedKeymap = definition;
            LastExpectedRevision = expectedRevision;
            var revision = expectedRevision is { } current ? current + 1 : 1;
            var stored = Store(definition, revision);
            Snapshot = Snapshot with
            {
                Keymaps =
                [
                    .. Snapshot.Keymaps
                                        .Where(item => item.Value.Id != definition.Id)
,
                    stored,
                ],
            };
            Changed?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromResult(DefinitionStoreResult<StoredDefinition<KeymapProfile>>
                .Success(stored));
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
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSavedWorkspace = definition;
            LastExpectedWorkspaceRevision = expectedRevision;
            var revision = expectedRevision is { } current ? current + 1 : 1;
            var stored = Store(definition, revision);
            Snapshot = Snapshot with
            {
                Workspaces =
                [
                    .. Snapshot.Workspaces
                                        .Where(item => item.Value.Id != definition.Id)
,
                    stored,
                ],
            };
            Changed?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Success(stored));
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
            ThemePreference definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>>
            SaveTerminalProfileAsync(
                TerminalProfile definition,
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
