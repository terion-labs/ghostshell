using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class TerminalConnectionSettingsViewModelTests
{
    [Fact]
    public void Existing_connection_editor_uses_the_catalog_value_and_revision()
    {
        var profile = Profile("connection.settings-owner", "Operations");
        var fixture = CreateCatalog(new DefinitionCatalogSnapshot(
            [Store(profile, 17)], [], [], [], [], [], [], [], []));
        using var viewModel = new TerminalConnectionSettingsViewModel(
            fixture.Catalog,
            new StubConnectionRuntime());

        var editor = viewModel.CreateEditor(profile.Id);

        Assert.True(editor.IsEditing);
        Assert.Equal(17, editor.ExpectedRevision);
        Assert.Equal("Operations", editor.Name);
    }

    [Fact]
    public void Missing_connection_is_rejected_before_an_editor_is_created()
    {
        var fixture = CreateCatalog(DefinitionCatalogSnapshot.Empty);
        using var viewModel = new TerminalConnectionSettingsViewModel(
            fixture.Catalog,
            new StubConnectionRuntime());

        var error = Assert.Throws<InvalidOperationException>(() =>
            viewModel.CreateEditor(new ConnectionId("connection.missing")));

        Assert.Equal("That connection no longer exists.", error.Message);
    }

    [Fact]
    public async Task Save_forwards_the_exact_profile_and_expected_revision()
    {
        var fixture = CreateCatalog(DefinitionCatalogSnapshot.Empty);
        using var viewModel = new TerminalConnectionSettingsViewModel(
            fixture.Catalog,
            new StubConnectionRuntime());
        var profile = Profile("connection.new", "New connection");

        var result = await viewModel.SaveAsync(
            new ConnectionEditorSaveRequest(profile, 23),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(profile, fixture.Proxy.LastSavedConnection);
        Assert.Equal(23, fixture.Proxy.LastExpectedConnectionRevision);
        Assert.Equal(24, result.Value?.Revision);
    }

    [Fact]
    public async Task Revision_conflict_is_returned_without_rewriting_it()
    {
        var fixture = CreateCatalog(DefinitionCatalogSnapshot.Empty);
        fixture.Proxy.RejectConnectionSave = true;
        using var viewModel = new TerminalConnectionSettingsViewModel(
            fixture.Catalog,
            new StubConnectionRuntime());

        var result = await viewModel.SaveAsync(
            new ConnectionEditorSaveRequest(
                Profile("connection.conflict", "Conflict"),
                8),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, result.Error?.Code);
        Assert.Equal(9, result.Error?.CurrentRevision);
    }

    [Fact]
    public async Task Disposed_owner_rejects_editor_creation_and_persistence()
    {
        var fixture = CreateCatalog(DefinitionCatalogSnapshot.Empty);
        var viewModel = new TerminalConnectionSettingsViewModel(
            fixture.Catalog,
            new StubConnectionRuntime());
        viewModel.Dispose();

        Assert.Throws<ObjectDisposedException>(() => viewModel.CreateEditor());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await viewModel.SaveAsync(
                new ConnectionEditorSaveRequest(
                    Profile("connection.disposed", "Disposed"),
                    null),
                CancellationToken.None));
    }

    private static ConnectionProfile Profile(string id, string name) => new(
        new ConnectionId(id),
        ConnectionProfile.CurrentSchemaVersion,
        name,
        new ConnectionEndpoint.Local("/bin/sh"),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);

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

        public bool RejectConnectionSave { get; set; }

        public ConnectionProfile? LastSavedConnection { get; private set; }

        public long? LastExpectedConnectionRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            return targetMethod.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                nameof(IDefinitionCatalog.SaveConnectionAsync) => SaveConnection(
                    (ConnectionProfile)args[0]!,
                    (long?)args[1]),
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>>
            SaveConnection(ConnectionProfile definition, long? expectedRevision)
        {
            LastSavedConnection = definition;
            LastExpectedConnectionRevision = expectedRevision;
            return ValueTask.FromResult(RejectConnectionSave
                ? DefinitionStoreResult<StoredDefinition<ConnectionProfile>>.Failure(new(
                    DefinitionStoreErrorCode.RevisionConflict,
                    "The connection changed before it could be saved.",
                    (expectedRevision ?? 0) + 1))
                : DefinitionStoreResult<StoredDefinition<ConnectionProfile>>.Success(
                    Store(definition, (expectedRevision ?? 0) + 1)));
        }
    }

    private sealed class StubConnectionRuntime : IConnectionRuntime
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
}
