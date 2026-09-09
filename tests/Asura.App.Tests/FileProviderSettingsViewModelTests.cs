using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class FileProviderSettingsViewModelTests
{
    [Fact]
    public void Catalog_and_live_runtime_are_combined_into_the_settings_projection()
    {
        var profile = Profile("files.settings-owner", "Settings files");
        var fixture = CreateCatalog(Snapshot(Store(profile, 12)));
        var runtime = new RecordingProviderRuntime();
        var liveProfiles = new List<FileProviderProfileDescriptor>
        {
            Descriptor(profile.Id, profile.Name),
        };
        using var viewModel = CreateViewModel(fixture.Catalog, runtime, liveProfiles);

        var item = Assert.Single(viewModel.Definitions);

        Assert.Equal(profile.Id, item.Id);
        Assert.Equal(12, item.Revision);
        Assert.Equal("Ready", item.Status);
        Assert.False(item.HasError);
        Assert.Same(liveProfiles, viewModel.Profiles);
    }

    [Fact]
    public void Runtime_profile_change_refreshes_health_on_the_injected_dispatcher()
    {
        var profile = Profile("files.runtime-refresh", "Runtime refresh");
        var fixture = CreateCatalog(Snapshot(Store(profile, 3)));
        var runtime = new RecordingProviderRuntime();
        var liveProfiles = new List<FileProviderProfileDescriptor>();
        var dispatcher = new RecordingDispatcher();
        using var viewModel = new FileProviderSettingsViewModel(
            fixture.Catalog,
            runtime,
            () => liveProfiles,
            () => [],
            dispatcher);
        Assert.Equal("Loading", Assert.Single(viewModel.Definitions).Status);
        liveProfiles.Add(Descriptor(profile.Id, profile.Name));

        runtime.RaiseProfilesChanged();

        Assert.Equal(1, dispatcher.InvocationCount);
        Assert.Equal("Ready", Assert.Single(viewModel.Definitions).Status);
    }

    [Fact]
    public void Existing_profile_editor_uses_catalog_revision_connections_and_scoped_secrets()
    {
        var profile = Profile("files.editor", "Editor files");
        var connection = SshConnection();
        var snapshot = Snapshot(Store(profile, 29)) with
        {
            Connections = [Store(connection, 5)],
        };
        var fixture = CreateCatalog(snapshot);
        var runtime = new RecordingProviderRuntime();
        var secret = Secret(profile.Id);
        using var viewModel = new FileProviderSettingsViewModel(
            fixture.Catalog,
            runtime,
            () => [],
            () => [secret],
            new RecordingDispatcher());

        var editor = viewModel.CreateEditor(profile.Id);

        Assert.Equal(29, editor.ExpectedRevision);
        Assert.Contains(editor.SecretOptions, option => option.Reference == secret.Reference);
    }

    [Fact]
    public async Task Save_forwards_the_exact_profile_and_expected_revision()
    {
        var fixture = CreateCatalog(DefinitionCatalogSnapshot.Empty);
        using var viewModel = CreateViewModel(
            fixture.Catalog,
            new RecordingProviderRuntime(),
            []);
        var profile = Profile("files.save", "Saved files");

        var result = await viewModel.SaveAsync(
            new FileProviderProfileSaveRequest(profile, 41),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(profile, fixture.Proxy.LastSavedProfile);
        Assert.Equal(41, fixture.Proxy.LastExpectedRevision);
        Assert.Equal(42, result.Value?.Revision);
    }

    [Fact]
    public void Dispose_releases_runtime_subscription_and_rejects_new_work()
    {
        var fixture = CreateCatalog(DefinitionCatalogSnapshot.Empty);
        var runtime = new RecordingProviderRuntime();
        var viewModel = CreateViewModel(fixture.Catalog, runtime, []);
        Assert.Equal(1, runtime.SubscriptionCount);

        viewModel.Dispose();

        Assert.Equal(0, runtime.SubscriptionCount);
        Assert.Throws<ObjectDisposedException>(() => viewModel.CreateEditor());
        Assert.Throws<ObjectDisposedException>(() =>
            viewModel.ApplyCatalog(DefinitionCatalogSnapshot.Empty));
    }

    private static FileProviderSettingsViewModel CreateViewModel(
        IDefinitionCatalog catalog,
        IFileProviderProfileRuntime runtime,
        IReadOnlyList<FileProviderProfileDescriptor> liveProfiles) =>
        new(catalog, runtime, () => liveProfiles, () => [], new RecordingDispatcher());

    private static DefinitionCatalogSnapshot Snapshot(
        StoredDefinition<FileProviderProfile> profile) =>
        DefinitionCatalogSnapshot.Empty with { FileProviderProfiles = [profile] };

    private static FileProviderProfile Profile(string id, string name) => new(
        new FileProviderProfileId(id),
        FileProviderProfile.CurrentSchemaVersion,
        name,
        new FileProviderConfiguration.Local("/tmp/asura-files"));

    private static FileProviderProfileDescriptor Descriptor(
        FileProviderProfileId id,
        string name)
    {
        var root = new FilePanelLocation(
            id.Value,
            "local",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        return new(
            id.Value,
            name,
            FileProviderFamily.Posix,
            root,
            FilePanelCapability.List,
            100,
            1024 * 1024);
    }

    private static ConnectionProfile SshConnection() => new(
        new ConnectionId("connection.files-settings"),
        ConnectionProfile.CurrentSchemaVersion,
        "SSH files",
        new ConnectionEndpoint.Ssh("files.example.test", username: "operator"),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.AcceptNew);

    private static SecretMetadataViewModel Secret(FileProviderProfileId id) => new(
        new SecretRef("secret.files-settings"),
        "File password",
        SecretKind.Password.ToString(),
        SecretScopeKind.FileProvider.ToString(),
        "now",
        "never",
        new SecretScope(SecretScopeKind.FileProvider, id.Value),
        "none",
        0);

    private static StoredDefinition<T> Store<T>(T value, long revision)
        where T : IDurableDefinition =>
        new(value, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static CatalogFixture CreateCatalog(DefinitionCatalogSnapshot snapshot)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingCatalogProxy>();
        var proxy = (RecordingCatalogProxy)(object)catalog;
        proxy.CurrentSnapshot = snapshot;
        return new(catalog, proxy);
    }

    private sealed record CatalogFixture(
        IDefinitionCatalog Catalog,
        RecordingCatalogProxy Proxy);

    public class RecordingCatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot CurrentSnapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public FileProviderProfile? LastSavedProfile { get; private set; }

        public long? LastExpectedRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            return targetMethod.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                nameof(IDefinitionCatalog.SaveFileProviderProfileAsync) => Save(
                    (FileProviderProfile)args[0]!,
                    (long?)args[1]),
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> Save(
            FileProviderProfile profile,
            long? expectedRevision)
        {
            LastSavedProfile = profile;
            LastExpectedRevision = expectedRevision;
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<FileProviderProfile>>.Success(
                    Store(profile, (expectedRevision ?? 0) + 1)));
        }
    }

    private sealed class RecordingProviderRuntime : IFileProviderProfileRuntime
    {
        private EventHandler? _profilesChanged;

        public int SubscriptionCount { get; private set; }

        public event EventHandler? ProfilesChanged
        {
            add
            {
                _profilesChanged += value;
                SubscriptionCount++;
            }
            remove
            {
                _profilesChanged -= value;
                SubscriptionCount--;
            }
        }

        public IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics { get; init; } = [];

        public void RaiseProfilesChanged() => _profilesChanged?.Invoke(this, EventArgs.Empty);

        public ValueTask<FileProviderTestResult> TestAsync(
            FileProviderProfile profile,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ReloadAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class RecordingDispatcher : IUiThreadDispatcher
    {
        public int InvocationCount { get; private set; }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            action();
            return Task.CompletedTask;
        }
    }
}
