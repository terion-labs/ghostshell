using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class McpServerSettingsViewModelTests
{
    [Fact]
    public void Existing_editor_uses_the_catalog_revision()
    {
        var profile = Profile();
        var (catalog, proxy) = Catalog();
        proxy.Snapshot = DefinitionCatalogSnapshot.Empty with
        {
            McpServerProfiles = [Store(profile, 17)],
        };
        using var viewModel = new McpServerSettingsViewModel(catalog, () => []);

        var editor = viewModel.CreateEditor(profile.Id);

        Assert.Equal(profile.Id.Value, editor.ProfileId);
        Assert.Equal(17, editor.ExpectedRevision);
    }

    [Fact]
    public async Task Authorized_save_forwards_the_exact_profile_and_revision()
    {
        var profile = Profile();
        var (catalog, proxy) = Catalog();
        using var viewModel = new McpServerSettingsViewModel(catalog, () => []);
        var request = Request(profile, expectedRevision: 23, requiresTrust: false);

        var result = await viewModel.SaveAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(profile, proxy.SavedProfile);
        Assert.Equal(23, proxy.ExpectedRevision);
    }

    [Fact]
    public async Task Unconfirmed_trust_review_is_rejected_before_persistence()
    {
        var profile = Profile();
        var (catalog, proxy) = Catalog();
        using var viewModel = new McpServerSettingsViewModel(catalog, () => []);
        var request = Request(profile, expectedRevision: 23, requiresTrust: true);

        var result = await viewModel.SaveAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, result.Error?.Code);
        Assert.Null(proxy.SavedProfile);
    }

    private static McpServerProfile Profile()
    {
        var workingDirectory = Path.GetFullPath(Path.GetTempPath());
        return new McpServerProfile(
            new McpServerProfileId("mcp.settings-owner"),
            McpServerProfile.CurrentSchemaVersion,
            "Settings owner",
            new McpServerTransport.Stdio(
                Path.Combine(workingDirectory, "asura-mcp-test"),
                [],
                workingDirectory,
                []),
            []);
    }

    private static McpServerProfileSaveRequest Request(
        McpServerProfile profile,
        long? expectedRevision,
        bool requiresTrust)
    {
        var stdio = Assert.IsType<McpServerTransport.Stdio>(profile.Transport);
        return new McpServerProfileSaveRequest(
            profile,
            expectedRevision,
            requiresTrust,
            isTrustConfirmed: false,
            new McpServerTrustReview(
                profile.Name,
                stdio.Executable,
                stdio.WorkingDirectory!,
                [],
                [],
                [],
                []));
    }

    private static (IDefinitionCatalog Catalog, CatalogProxy Proxy) Catalog()
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, CatalogProxy>();
        return (catalog, (CatalogProxy)(object)catalog);
    }

    private static StoredDefinition<T> Store<T>(T value, long revision)
        where T : IDurableDefinition =>
        new(value, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    public class CatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot Snapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public McpServerProfile? SavedProfile { get; private set; }

        public long? ExpectedRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => Snapshot,
                "add_Changed" or "remove_Changed" => null,
                nameof(IDefinitionCatalog.SaveMcpServerProfileAsync) => Save(args!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private object Save(object?[] args)
        {
            SavedProfile = (McpServerProfile)args[0]!;
            ExpectedRevision = (long?)args[1];
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<McpServerProfile>>.Success(
                    Store(SavedProfile, (ExpectedRevision ?? 0) + 1)));
        }
    }
}
