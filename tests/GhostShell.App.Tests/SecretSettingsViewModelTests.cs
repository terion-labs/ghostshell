using System.Reflection;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class SecretSettingsViewModelTests
{
    [Fact]
    public async Task Draft_ai_provider_can_store_a_key_before_catalog_save_and_refreshes_projection()
    {
        var fixture = CreateFixture(DefinitionCatalogSnapshot.Empty);
        using var viewModel = fixture.CreateViewModel();
        var profileId = AiProviderProfileId.New();
        var request = new CreateSecretRequest(SecretRef.New(), "Draft API key", SecretKind.ApiKey,
            new SecretScope(SecretScopeKind.AiProvider, profileId.Value),
            new SecretUsePurpose(SecretUseKind.UserManagement, profileId.Value));
        using var material = SecretMaterial.CopyFrom("synthetic-key"u8);

        var result = await viewModel.CreateAiProviderDraftAsync(request, material, CancellationToken.None);

        Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(result);
        Assert.Equal(request, fixture.Vault.CreateRequest);
        Assert.Equal(request.Reference, Assert.Single(viewModel.Secrets).Reference);
    }

    [Fact]
    public async Task Draft_ai_provider_rejects_other_credential_scopes_before_vault_mutation()
    {
        var fixture = CreateFixture(DefinitionCatalogSnapshot.Empty);
        using var viewModel = fixture.CreateViewModel();
        var request = new CreateSecretRequest(SecretRef.New(), "Connection key", SecretKind.ApiKey,
            new SecretScope(SecretScopeKind.Connection, "connection"),
            new SecretUsePurpose(SecretUseKind.UserManagement, "connection"));
        using var material = SecretMaterial.CopyFrom("synthetic-key"u8);

        var result = await viewModel.CreateAiProviderDraftAsync(request, material, CancellationToken.None);

        Assert.IsType<SecretVaultResult<SecretMetadata>.Failure>(result);
        Assert.Null(fixture.Vault.CreateRequest);
        Assert.Empty(viewModel.Secrets);
    }

    [Fact]
    public async Task File_provider_credential_creation_mutates_vault_and_refreshes_projection()
    {
        var profile = LocalProfile("files.secret-owner", "Secret owner");
        var fixture = CreateFixture(DefinitionCatalogSnapshot.Empty with
        {
            FileProviderProfiles = [Store(profile)],
        });
        using var viewModel = fixture.CreateViewModel();
        var projectionChanges = 0;
        viewModel.ProjectionChanged += (_, _) => projectionChanges++;

        var created = await viewModel.CreateFileProviderAsync(
            profile.Id,
            "Production password",
            SecretKind.Password,
            "not-exposed",
            CancellationToken.None);

        Assert.True(created);
        Assert.Equal(profile.Id.Value, fixture.Vault.CreateRequest?.Scope.OwnerId);
        Assert.Equal("Production password", Assert.Single(viewModel.Secrets).Label);
        Assert.False(viewModel.HasNoSecrets);
        Assert.Equal(1, projectionChanges);
    }

    [Fact]
    public async Task Referenced_credential_is_rejected_before_vault_deletion()
    {
        var reference = new SecretRef("secret.in-use");
        var profile = new FileProviderProfile(
            new FileProviderProfileId("files.ftp"),
            FileProviderProfile.CurrentSchemaVersion,
            "Production FTP",
            new FileProviderConfiguration.Ftp(
                "files.example.test",
                21,
                "operator",
                reference,
                FtpSecurityMode.ExplicitTls,
                FtpConnectionMode.AutoPassive));
        var fixture = CreateFixture(DefinitionCatalogSnapshot.Empty with
        {
            FileProviderProfiles = [Store(profile)],
        });
        using var viewModel = fixture.CreateViewModel();
        var secret = Secret(reference, profile.Id);

        var deleted = await viewModel.DeleteAsync(secret, CancellationToken.None);

        Assert.False(deleted);
        Assert.Equal(0, fixture.Vault.DeleteCount);
        Assert.Contains("file provider Production FTP", fixture.Errors.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_rejects_future_vault_work()
    {
        var fixture = CreateFixture(DefinitionCatalogSnapshot.Empty);
        var viewModel = fixture.CreateViewModel();

        viewModel.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            viewModel.ReportStatus("No longer available"));
    }

    private static Fixture CreateFixture(DefinitionCatalogSnapshot snapshot)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, CatalogProxy>();
        ((CatalogProxy)(object)catalog).Snapshot = snapshot;
        var vault = DispatchProxy.Create<ISecretVault, VaultProxy>();
        return new(catalog, (VaultProxy)(object)vault, []);
    }

    private static FileProviderProfile LocalProfile(string id, string name) => new(
        new FileProviderProfileId(id),
        FileProviderProfile.CurrentSchemaVersion,
        name,
        new FileProviderConfiguration.Local("/tmp/ghostshell-secret-owner"));

    private static StoredDefinition<T> Store<T>(T definition)
        where T : IDurableDefinition =>
        new(definition, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static SecretMetadataViewModel Secret(
        SecretRef reference,
        FileProviderProfileId profileId) => new(
        reference,
        "FTP password",
        nameof(SecretKind.Password),
        "File provider",
        "Never",
        "Never",
        new SecretScope(SecretScopeKind.FileProvider, profileId.Value),
        "Used",
        1);

    private sealed record Fixture(
        IDefinitionCatalog Catalog,
        VaultProxy Vault,
        List<string> Errors)
    {
        public SecretSettingsViewModel CreateViewModel() => new(
            Catalog,
            (ISecretVault)(object)Vault,
            null,
            null,
            null,
            _ => { },
            () => { },
            Errors.Add);
    }

    public class CatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot Snapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => Snapshot,
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
    }

    public class VaultProxy : DispatchProxy
    {
        private IReadOnlyList<SecretMetadata> _metadata = [];

        public CreateSecretRequest? CreateRequest { get; private set; }

        public int DeleteCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            args ??= [];
            if (targetMethod?.Name == nameof(ISecretVault.CreateAsync)
                && args is
                [
                    CreateSecretRequest request,
                    SecretMaterial,
                    CancellationToken,
                ])
            {
                CreateRequest = request;
                var metadata = new SecretMetadata(
                    request.Reference,
                    request.Label,
                    request.Kind,
                    request.Scope,
                    SecretVaultPersistenceKind.MemoryOnly,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch);
                _metadata = [metadata];
                return ValueTask.FromResult(
                    SecretVaultResult<SecretMetadata>.Succeed(metadata));
            }

            if (targetMethod?.Name == nameof(ISecretVault.DeleteAsync))
            {
                DeleteCount++;
                return ValueTask.FromResult(
                    SecretVaultResult<Unit>.Succeed(Unit.Value));
            }

            return targetMethod?.Name switch
            {
                "get_Availability" => new SecretVaultAvailability(
                    SecretVaultAvailabilityState.Available,
                    SecretVaultPersistenceKind.MemoryOnly,
                    SecretVaultCapabilities.All,
                    "test",
                    "test_available",
                    "Test vault is available."),
                nameof(ISecretVault.ListMetadataAsync) =>
                    ValueTask.FromResult(
                        SecretVaultResult<IReadOnlyList<SecretMetadata>>
                            .Succeed(_metadata)),
                nameof(IDisposable.Dispose) => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
        }
    }
}
