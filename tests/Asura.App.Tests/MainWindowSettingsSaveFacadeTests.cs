using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class MainWindowSettingsSaveFacadeTests
{
    [Theory]
    [InlineData(SettingsPage.Secrets)]
    [InlineData(SettingsPage.Mcp)]
    public void Secret_metadata_is_deferred_until_explicit_settings_navigation(
        SettingsPage page)
    {
        var vault = new EmptySecretVault();
        using var viewModel = CreateViewModel(
            new RecordingDefinitionCatalog(SettingsSnapshot()),
            vault);

        Assert.Equal(0, vault.ListMetadataCount);
        viewModel.SettingsPage = page;
        Assert.Equal(0, vault.ListMetadataCount);

        viewModel.ShowSettings(page);

        Assert.Equal(1, vault.ListMetadataCount);
    }

    [Fact]
    public async Task Theme_save_forwards_the_complete_preference_and_current_revision()
    {
        var catalog = new RecordingDefinitionCatalog(SettingsSnapshot());
        using var viewModel = CreateViewModel(catalog);
        var accent = AccentPreference.Custom(RgbColor.Parse("#5A8DEE"));

        var result = await viewModel.SaveThemeAsync(
            AppearanceMode.Dark,
            PlatformProfile.Gnome,
            accent,
            textScaleOverride: 1.25,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var save = Assert.IsType<RecordedSave<ThemePreference>>(catalog.ThemeSave);
        Assert.Equal(
            new ThemePreference(
                ThemePreference.Default.Id,
                ThemePreference.Default.Name,
                AppearanceMode.Dark,
                PlatformProfile.Gnome,
                accent,
                textScaleOverride: 1.25),
            save.Definition);
        Assert.Equal(ThemeRevision, save.ExpectedRevision);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Terminal_save_forwards_the_live_draft_and_editor_revision()
    {
        var catalog = new RecordingDefinitionCatalog(SettingsSnapshot());
        using var viewModel = CreateViewModel(catalog);
        var editor = Assert.IsType<TerminalProfileEditorViewModel>(
            viewModel.TerminalSettingsEditor);
        editor.FontFamily = "Cascadia Code";
        editor.FontSize = 17.5;
        editor.CursorStyle = TerminalCursorStyle.Bar;
        var expected = editor.CreateSaveRequest();

        var result = await viewModel.SaveTerminalProfileAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var save = Assert.IsType<RecordedSave<TerminalProfile>>(
            catalog.TerminalSave);
        Assert.Equivalent(expected.Profile, save.Definition, strict: true);
        Assert.Equal(expected.ExpectedRevision, save.ExpectedRevision);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Quick_terminal_save_forwards_the_live_draft_and_editor_revision()
    {
        var catalog = new RecordingDefinitionCatalog(SettingsSnapshot());
        using var viewModel = CreateViewModel(catalog);
        var editor = Assert.IsType<QuickTerminalSettingsEditorViewModel>(
            viewModel.QuickTerminalSettingsEditor);
        editor.HeightPercent = 64;
        editor.OpacityPercent = 91;
        editor.MonitorPolicy = QuickTerminalMonitorPolicy.Primary;
        var expected = editor.CreateSaveRequest();

        var result = await viewModel.SaveQuickTerminalSettingsAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var save = Assert.IsType<RecordedSave<QuickTerminalSettings>>(
            catalog.QuickTerminalSave);
        Assert.Equal(expected.Settings, save.Definition);
        Assert.Equal(expected.ExpectedRevision, save.ExpectedRevision);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Connection_save_forwards_the_exact_request()
    {
        var catalog = new RecordingDefinitionCatalog(SettingsSnapshot());
        using var viewModel = CreateViewModel(catalog);
        var profile = new ConnectionProfile(
            new ConnectionId("connection.settings-save"),
            ConnectionProfile.CurrentSchemaVersion,
            "Settings save",
            new ConnectionEndpoint.Local("/bin/zsh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable,
            ["local", "settings"]);
        var request = new ConnectionEditorSaveRequest(profile, 44);

        var result = await viewModel.SaveConnectionAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var save = Assert.IsType<RecordedSave<ConnectionProfile>>(
            catalog.ConnectionSave);
        Assert.Same(profile, save.Definition);
        Assert.Equal(request.ExpectedRevision, save.ExpectedRevision);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task File_provider_save_forwards_the_exact_request()
    {
        var catalog = new RecordingDefinitionCatalog(SettingsSnapshot());
        using var viewModel = CreateViewModel(catalog);
        var profile = new FileProviderProfile(
            new FileProviderProfileId("files.settings-save"),
            FileProviderProfile.CurrentSchemaVersion,
            "Settings save",
            new FileProviderConfiguration.Local("/tmp/asura-settings"));
        var request = new FileProviderProfileSaveRequest(profile, 45);

        var result = await viewModel.SaveFileProviderProfileAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var save = Assert.IsType<RecordedSave<FileProviderProfile>>(
            catalog.FileProviderSave);
        Assert.Same(profile, save.Definition);
        Assert.Equal(request.ExpectedRevision, save.ExpectedRevision);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Ai_provider_save_forwards_the_exact_request()
    {
        var catalog = new RecordingDefinitionCatalog(SettingsSnapshot());
        using var viewModel = CreateViewModel(catalog);
        var profile = new AiProviderProfile(
            new AiProviderProfileId("ai.settings-save"),
            AiProviderProfile.CurrentSchemaVersion,
            "Settings save",
            AiProviderKind.OpenAiCompatible,
            new Uri("http://127.0.0.1:11434/v1/"),
            new AiProviderAuthentication.None(),
            "qwen3",
            order: 7);
        var request = new AiProviderProfileSaveRequest(profile, 46);

        var result = await viewModel.SaveAiProviderProfileAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var save = Assert.IsType<RecordedSave<AiProviderProfile>>(
            catalog.AiProviderSave);
        Assert.Same(profile, save.Definition);
        Assert.Equal(request.ExpectedRevision, save.ExpectedRevision);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Mcp_server_save_forwards_the_exact_authorized_request()
    {
        var catalog = new RecordingDefinitionCatalog(SettingsSnapshot());
        using var viewModel = CreateViewModel(catalog);
        var workingDirectory = Path.GetFullPath(Path.GetTempPath());
        var profile = new McpServerProfile(
            new McpServerProfileId("mcp.settings-save"),
            McpServerProfile.CurrentSchemaVersion,
            "Settings save",
            new McpServerTransport.Stdio(
                Path.Combine(workingDirectory, "asura-mcp-test"),
                ["--stdio"],
                workingDirectory,
                []),
            ["inspect"]);
        var request = new McpServerProfileSaveRequest(
            profile,
            expectedRevision: 47,
            requiresTrustConfirmation: false,
            isTrustConfirmed: false,
            new McpServerTrustReview(
                profile.Name,
                Assert.IsType<McpServerTransport.Stdio>(profile.Transport).Executable,
                Assert.IsType<McpServerTransport.Stdio>(profile.Transport).WorkingDirectory!,
                [],
                [],
                [],
                []));

        var result = await viewModel.SaveMcpServerProfileAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var save = Assert.IsType<RecordedSave<McpServerProfile>>(
            catalog.McpServerSave);
        Assert.Same(profile, save.Definition);
        Assert.Equal(request.ExpectedRevision, save.ExpectedRevision);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Prompted_database_password_is_vaulted_and_linked_to_the_profile()
    {
        var profile = new DatabaseConnectionProfile(
            new DatabaseConnectionProfileId("database.settings-save"),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "Production database",
            "postgres",
            "Host=db.internal;Database=app");
        var catalog = new RecordingDefinitionCatalog(
            SettingsSnapshot() with
            {
                DatabaseConnections = [Store(profile, 48)],
            });
        using var vault = new TestPersistentVault();
        using var viewModel = CreateViewModel(catalog, vault);

        var saved = await viewModel.StoreDatabasePasswordAsync(
            profile.Id,
            "typed-password");

        Assert.NotNull(saved);
        var save = Assert.IsType<RecordedSave<DatabaseConnectionProfile>>(
            catalog.DatabaseSave);
        Assert.Equal(48, save.ExpectedRevision);
        var reference = Assert.IsType<SecretRef>(save.Definition.PasswordSecret);
        Assert.DoesNotContain(
            "typed-password",
            save.Definition.ConnectionString,
            StringComparison.Ordinal);

        using var material = Assert.IsType<SecretVaultResult<SecretMaterial>.Success>(
            await vault.ResolveAsync(
                new ResolveSecretRequest(
                    reference,
                    new SecretScope(SecretScopeKind.DatabaseConnection, profile.Id.Value),
                    new SecretUsePurpose(
                        SecretUseKind.DatabaseConnectionAuthentication,
                        profile.Id.Value)),
                default)).Value;
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        try
        {
            Assert.Equal("typed-password", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    [Fact]
    public async Task Failed_terminal_save_preserves_the_live_draft_and_error()
    {
        var error = new DefinitionStoreError(
            DefinitionStoreErrorCode.StorageUnavailable,
            "The settings store is unavailable.");
        var catalog = new RecordingDefinitionCatalog(SettingsSnapshot())
        {
            SaveError = error,
        };
        using var viewModel = CreateViewModel(catalog);
        var editor = Assert.IsType<TerminalProfileEditorViewModel>(
            viewModel.TerminalSettingsEditor);
        editor.FontFamily = "Unsaved terminal draft";

        var result = await viewModel.SaveTerminalProfileAsync(
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Same(error, result.Error);
        Assert.Same(editor, viewModel.TerminalSettingsEditor);
        Assert.Equal("Unsaved terminal draft", editor.FontFamily);
        Assert.Equal(error.Message, viewModel.OperationError);
    }

    [Fact]
    public async Task Cancelled_quick_terminal_save_preserves_the_live_draft_and_error()
    {
        var error = new DefinitionStoreError(
            DefinitionStoreErrorCode.Cancelled,
            "The Quick Terminal save was cancelled.");
        var catalog = new RecordingDefinitionCatalog(SettingsSnapshot())
        {
            SaveError = error,
        };
        using var viewModel = CreateViewModel(catalog);
        var editor = Assert.IsType<QuickTerminalSettingsEditorViewModel>(
            viewModel.QuickTerminalSettingsEditor);
        editor.HeightPercent = 73;

        var result = await viewModel.SaveQuickTerminalSettingsAsync(
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Same(error, result.Error);
        Assert.Same(editor, viewModel.QuickTerminalSettingsEditor);
        Assert.Equal(73, editor.HeightPercent);
        Assert.Equal(error.Message, viewModel.OperationError);
    }

    private const long ThemeRevision = 41;
    private const long TerminalRevision = 42;
    private const long QuickTerminalRevision = 43;

    private static MainWindowViewModel CreateViewModel(
        IDefinitionCatalog catalog,
        ISecretVault? secretVault = null)
    {
        var files = new EmptyFileClients();
        return new MainWindowViewModel(
            DispatchProxy.Create<ISessionHostClient, RejectingSessionHostProxy>(),
            catalog,
            new UnusedConnectionRuntime(),
            secretVault ?? new EmptySecretVault(),
            files,
            files,
            new TerminalStartupCommandDispatcher(
                new SuccessfulAuditStore(),
                TimeProvider.System));
    }

    private static DefinitionCatalogSnapshot SettingsSnapshot() =>
        DefinitionCatalogSnapshot.Empty with
        {
            Themes =
            [
                Store(ThemePreference.Default, ThemeRevision),
            ],
            TerminalProfiles =
            [
                Store(DefaultTerminalProfile(), TerminalRevision),
            ],
            QuickTerminalSettings =
            [
                Store(QuickTerminalSettings.Default, QuickTerminalRevision),
            ],
        };

    private static TerminalProfile DefaultTerminalProfile() =>
        new(
            new TerminalProfileId("builtin.terminal.settings-save"),
            "Settings terminal",
            "JetBrains Mono",
            14,
            1.4,
            TerminalCursorStyle.Block,
            cursorBlink: true,
            100_000,
            TerminalPalette.AsuraDark,
            BuiltInKeymaps.LinuxTerminalId);

    private static StoredDefinition<T> Store<T>(T value, long revision)
        where T : IDurableDefinition =>
        new(
            value,
            revision,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private sealed record RecordedSave<T>(
        T Definition,
        long? ExpectedRevision)
        where T : IDurableDefinition;

    private sealed class RecordingDefinitionCatalog(
        DefinitionCatalogSnapshot snapshot)
        : IDefinitionCatalog
    {
        public DefinitionCatalogSnapshot Snapshot { get; } = snapshot;

        public DefinitionStoreError? SaveError { get; init; }

        public RecordedSave<ThemePreference>? ThemeSave { get; private set; }

        public RecordedSave<TerminalProfile>? TerminalSave { get; private set; }

        public RecordedSave<QuickTerminalSettings>? QuickTerminalSave
        {
            get;
            private set;
        }

        public RecordedSave<ConnectionProfile>? ConnectionSave
        {
            get;
            private set;
        }

        public RecordedSave<FileProviderProfile>? FileProviderSave
        {
            get;
            private set;
        }

        public RecordedSave<AiProviderProfile>? AiProviderSave
        {
            get;
            private set;
        }

        public RecordedSave<McpServerProfile>? McpServerSave
        {
            get;
            private set;
        }

        public RecordedSave<DatabaseConnectionProfile>? DatabaseSave
        {
            get;
            private set;
        }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>>
            InitializeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>>
            ReloadAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>>
            SaveConnectionAsync(
                ConnectionProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            ConnectionSave = new(definition, expectedRevision);
            return Complete(definition, expectedRevision);
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>>
            SaveLayoutAsync(
                LayoutDefinition definition,
                long? expectedRevision,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>>
            SaveScreenAsync(
                ScreenDefinition definition,
                long? expectedRevision,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
            SaveWorkspaceAsync(
                WorkspaceDefinition definition,
                long? expectedRevision,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>>
            SaveThemeAsync(
                ThemePreference definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            ThemeSave = new(definition, expectedRevision);
            return Complete(definition, expectedRevision);
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>>
            SaveTerminalProfileAsync(
                TerminalProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            TerminalSave = new(definition, expectedRevision);
            return Complete(definition, expectedRevision);
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>>
            SaveKeymapAsync(
                KeymapProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>>
            SaveFileProviderProfileAsync(
                FileProviderProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            FileProviderSave = new(definition, expectedRevision);
            return Complete(definition, expectedRevision);
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>>
            SaveAiProviderProfileAsync(
                AiProviderProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            AiProviderSave = new(definition, expectedRevision);
            return Complete(definition, expectedRevision);
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>>
            SaveMcpServerProfileAsync(
                McpServerProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            McpServerSave = new(definition, expectedRevision);
            return Complete(definition, expectedRevision);
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>>
            SaveQuickTerminalSettingsAsync(
                QuickTerminalSettings definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            QuickTerminalSave = new(
                definition,
                expectedRevision);
            return Complete(definition, expectedRevision);
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<DatabaseConnectionProfile>>>
            SaveDatabaseConnectionAsync(
                DatabaseConnectionProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken)
        {
            DatabaseSave = new(definition, expectedRevision);
            return Complete(definition, expectedRevision);
        }

        public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
            DefinitionKey key,
            long expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private ValueTask<DefinitionStoreResult<StoredDefinition<T>>> Complete<T>(
            T definition,
            long? expectedRevision)
            where T : IDurableDefinition
        {
            var result = SaveError is null
                ? DefinitionStoreResult<StoredDefinition<T>>.Success(
                    Store(definition, (expectedRevision ?? 0) + 1))
                : DefinitionStoreResult<StoredDefinition<T>>.Failure(SaveError);
            return ValueTask.FromResult(result);
        }
    }

    public class RejectingSessionHostProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class UnusedConnectionRuntime : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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

        public int ListMetadataCount { get; private set; }

        public void Dispose()
        {
        }

        public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>>
            ListMetadataAsync(
                ListSecretMetadataRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListMetadataCount++;
            return ValueTask.FromResult(
                SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([]));
        }

        public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
            CreateSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
            ReplaceSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
            RelabelSecretRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
            DeleteSecretRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
            GetSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestPersistentVault : ISecretVault
    {
        private readonly Dictionary<SecretRef, (SecretMetadata Metadata, byte[] Value)>
            _entries = [];

        public SecretVaultAvailability Availability { get; } = new(
            SecretVaultAvailabilityState.Available,
            SecretVaultPersistenceKind.OsProtectedPersistent,
            SecretVaultCapabilities.All,
            "test-keychain",
            "test_keychain",
            "Test system credential store");

        public void Dispose()
        {
            foreach (var entry in _entries.Values)
            {
                CryptographicOperations.ZeroMemory(entry.Value);
            }

            _entries.Clear();
        }

        public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
            CreateSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = new byte[material.Length];
            material.CopyTo(value);
            var metadata = new SecretMetadata(
                request.Reference,
                request.Label,
                request.Kind,
                request.Scope,
                SecretVaultPersistenceKind.OsProtectedPersistent,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
            _entries.Add(request.Reference, (metadata, value));
            return ValueTask.FromResult(
                SecretVaultResult<SecretMetadata>.Succeed(metadata));
        }

        public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _entries.TryGetValue(request.Reference, out var entry)
                ? ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Succeed(
                    SecretMaterial.CopyFrom(entry.Value)))
                : ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Fail(
                    SecretVaultError.Create(SecretVaultErrorCode.NotFound)));
        }

        public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
            ReplaceSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
            RelabelSecretRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
            DeleteSecretRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_entries.Remove(request.Reference, out var entry))
            {
                return ValueTask.FromResult(SecretVaultResult<Unit>.Fail(
                    SecretVaultError.Create(SecretVaultErrorCode.NotFound)));
            }

            CryptographicOperations.ZeroMemory(entry.Value);
            return ValueTask.FromResult(SecretVaultResult<Unit>.Succeed(Unit.Value));
        }

        public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
            GetSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            _entries.TryGetValue(request.Reference, out var entry)
                ? ValueTask.FromResult(
                    SecretVaultResult<SecretMetadata>.Succeed(entry.Metadata))
                : ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Fail(
                    SecretVaultError.Create(SecretVaultErrorCode.NotFound)));

        public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
            ListSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed(
                    [.. _entries.Values.Select(entry => entry.Metadata)]));
    }

    private sealed class EmptyFileClients
        : IFilePanelClient, IFileTransferQueueClient
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
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
            FilePanelTransferRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<Unit>> CancelAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
