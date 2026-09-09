using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class AiProviderSettingsViewModelTests
{
    [Fact]
    public void Catalog_and_runtime_are_combined_into_the_settings_projection()
    {
        var profile = Profile("ai.settings-owner", "Settings AI", 2);
        var fixture = CreateCatalog(Snapshot(Store(profile, 17)));
        var runtime = new RecordingRuntime
        {
            Profiles = [Descriptor(profile)],
        };
        using var viewModel = CreateViewModel(fixture.Catalog, runtime);

        var item = Assert.Single(viewModel.Definitions);

        Assert.Equal(profile.Id, item.Id);
        Assert.Equal(17, item.Revision);
        Assert.Equal("Ready", item.Status);
        Assert.True(viewModel.HasProviders);
        Assert.False(viewModel.HasNoProviders);
    }

    [Fact]
    public void Runtime_change_refreshes_projection_and_notifies_policy_host()
    {
        var profile = Profile("ai.runtime-refresh", "Runtime refresh", 0);
        var fixture = CreateCatalog(Snapshot(Store(profile, 4)));
        var runtime = new RecordingRuntime();
        var dispatcher = new RecordingDispatcher();
        using var viewModel = new AiProviderSettingsViewModel(
            fixture.Catalog,
            runtime,
            null,
            () => [],
            dispatcher);
        var notifications = 0;
        viewModel.RuntimeProfilesChanged += (_, _) => notifications++;
        runtime.Profiles = [Descriptor(profile)];

        runtime.RaiseProfilesChanged();

        Assert.Equal(1, dispatcher.InvocationCount);
        Assert.Equal(1, notifications);
        Assert.Equal("Ready", Assert.Single(viewModel.Definitions).Status);
    }

    [Fact]
    public void Existing_editor_uses_revision_and_new_editor_uses_next_free_order()
    {
        var profile = Profile("ai.editor", "Editor", 0);
        var second = Profile("ai.second", "Second", 2);
        var fixture = CreateCatalog(DefinitionCatalogSnapshot.Empty with
        {
            AiProviderProfiles = [Store(profile, 31), Store(second, 8)],
        });
        using var viewModel = CreateViewModel(fixture.Catalog, new RecordingRuntime());

        var existing = viewModel.CreateEditor(profile.Id);
        var created = viewModel.CreateEditor();

        Assert.Equal(31, existing.ExpectedRevision);
        Assert.Equal(1, created.Order);
    }

    [Fact]
    public async Task Save_forwards_exact_identity_and_dispose_releases_subscription()
    {
        var fixture = CreateCatalog(DefinitionCatalogSnapshot.Empty);
        var runtime = new RecordingRuntime();
        var viewModel = CreateViewModel(fixture.Catalog, runtime);
        var profile = Profile("ai.save", "Save", 5);

        var result = await viewModel.SaveAsync(
            new AiProviderProfileSaveRequest(profile, 22),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(profile, fixture.Proxy.LastSavedProfile);
        Assert.Equal(22, fixture.Proxy.LastExpectedRevision);
        viewModel.Dispose();
        Assert.Equal(0, runtime.SubscriptionCount);
        Assert.Throws<ObjectDisposedException>(() => viewModel.CreateEditor());
    }

    private static AiProviderSettingsViewModel CreateViewModel(
        IDefinitionCatalog catalog,
        RecordingRuntime runtime) =>
        new(catalog, runtime, null, () => [], new RecordingDispatcher());

    private static AiProviderProfile Profile(string id, string name, int order) => new(
        new AiProviderProfileId(id),
        AiProviderProfile.CurrentSchemaVersion,
        name,
        AiProviderKind.OpenAiCompatible,
        new Uri("http://127.0.0.1:11434/v1/"),
        new AiProviderAuthentication.None(),
        "local-model",
        order);

    private static AiProviderProfileDescriptor Descriptor(AiProviderProfile profile) => new(
        profile.Id,
        profile.Name,
        profile.ProviderKind,
        profile.Endpoint,
        profile.DefaultModel,
        profile.Order,
        profile.IsEnabled,
        RequiresCredential: false);

    private static DefinitionCatalogSnapshot Snapshot(
        StoredDefinition<AiProviderProfile> profile) =>
        DefinitionCatalogSnapshot.Empty with { AiProviderProfiles = [profile] };

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

        public AiProviderProfile? LastSavedProfile { get; private set; }

        public long? LastExpectedRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            return targetMethod.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                nameof(IDefinitionCatalog.SaveAiProviderProfileAsync) => Save(
                    (AiProviderProfile)args[0]!,
                    (long?)args[1]),
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> Save(
            AiProviderProfile profile,
            long? expectedRevision)
        {
            LastSavedProfile = profile;
            LastExpectedRevision = expectedRevision;
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<AiProviderProfile>>.Success(
                    Store(profile, (expectedRevision ?? 0) + 1)));
        }
    }

    private sealed class RecordingRuntime : IAiProviderProfileRuntime
    {
        private EventHandler? _profilesChanged;

        public int SubscriptionCount { get; private set; }

        public event EventHandler? ProfilesChanged
        {
            add { _profilesChanged += value; SubscriptionCount++; }
            remove { _profilesChanged -= value; SubscriptionCount--; }
        }

        public IReadOnlyList<AiProviderProfileDescriptor> Profiles { get; set; } = [];

        public IReadOnlyList<AiProviderRuntimeDiagnostic> Diagnostics { get; set; } = [];

        public void RaiseProfilesChanged() => _profilesChanged?.Invoke(this, EventArgs.Empty);

        public ValueTask<AiProviderTestResult> TestAsync(
            AiProviderProfile profile,
            CancellationToken cancellationToken) => throw new NotSupportedException();

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
