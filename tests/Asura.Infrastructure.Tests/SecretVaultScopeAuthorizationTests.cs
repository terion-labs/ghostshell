using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SecretVaultScopeAuthorizationTests
{
    private static readonly SecretScope Scope = new(SecretScopeKind.Connection, "production-ssh");
    private static readonly SecretUsePurpose Purpose = new(
        SecretUseKind.ConnectionAuthentication,
        "production-ssh");
    private static readonly SecretScope OtherScope = new(SecretScopeKind.Connection, "other-ssh");
    private static readonly SecretUsePurpose OtherPurpose = new(
        SecretUseKind.ConnectionAuthentication,
        "other-ssh");

    [Fact]
    public async Task Wrong_target_or_purpose_is_denied_without_revealing_existence()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        using var material = SecretMaterial.CopyFrom([7, 8, 9]);
        Success(await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "SSH password",
                SecretKind.Password,
                Scope,
                Purpose),
            material,
            default));

        var wrongKind = new SecretUsePurpose(
            SecretUseKind.AiProviderAuthentication,
            Scope.OwnerId!);
        using var replacement = SecretMaterial.CopyFrom([4, 5, 6]);
        AssertDenied(await vault.ReplaceAsync(
            new ReplaceSecretRequest(reference, Scope, wrongKind),
            replacement,
            default));
        AssertDenied(await vault.RelabelAsync(
            new RelabelSecretRequest(reference, Scope, "Hidden change", wrongKind),
            default));
        AssertDenied(await vault.GetMetadataAsync(
            new GetSecretMetadataRequest(reference, Scope, wrongKind),
            default));
        AssertDenied(await vault.ListMetadataAsync(
            new ListSecretMetadataRequest(Scope, wrongKind),
            default));
        AssertDenied(await vault.DeleteAsync(
            new DeleteSecretRequest(reference, Scope, wrongKind),
            default));

        var known = Failure(await vault.ResolveAsync(
            new ResolveSecretRequest(reference, Scope, OtherPurpose),
            default));
        var unknown = Failure(await vault.ResolveAsync(
            new ResolveSecretRequest(SecretRef.New(), Scope, OtherPurpose),
            default));

        Assert.Equal(SecretVaultErrorCode.AccessDenied, known.Code);
        Assert.Equal(known.StableCode, unknown.StableCode);
        Assert.Equal(known.Message, unknown.Message);
    }

    [Fact]
    public async Task Claimed_scope_must_match_the_scope_stored_with_the_secret()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        using var original = SecretMaterial.CopyFrom([1, 2, 3]);
        Success(await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "SSH password",
                SecretKind.Password,
                Scope,
                Purpose),
            original,
            default));

        using var replacement = SecretMaterial.CopyFrom([4, 5, 6]);
        AssertDenied(await vault.ResolveAsync(
            new ResolveSecretRequest(reference, OtherScope, OtherPurpose),
            default));
        AssertDenied(await vault.ReplaceAsync(
            new ReplaceSecretRequest(reference, OtherScope, OtherPurpose),
            replacement,
            default));
        AssertDenied(await vault.RelabelAsync(
            new RelabelSecretRequest(reference, OtherScope, "Changed", OtherPurpose),
            default));
        AssertDenied(await vault.GetMetadataAsync(
            new GetSecretMetadataRequest(reference, OtherScope, OtherPurpose),
            default));
        AssertDenied(await vault.DeleteAsync(
            new DeleteSecretRequest(reference, OtherScope, OtherPurpose),
            default));

        var metadata = Success(await vault.GetMetadataAsync(
            new GetSecretMetadataRequest(reference, Scope, Purpose),
            default));
        Assert.Equal("SSH password", metadata.Label);
        using var resolved = Success(await vault.ResolveAsync(
            new ResolveSecretRequest(reference, Scope, Purpose),
            default));
        var bytes = new byte[resolved.Length];
        try
        {
            resolved.CopyTo(bytes);
            Assert.Equal([1, 2, 3], bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    [Fact]
    public async Task Management_can_manage_matching_metadata_but_cannot_resolve_material()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        var management = new SecretUsePurpose(SecretUseKind.UserManagement, Scope.OwnerId!);
        using var material = SecretMaterial.CopyFrom([1]);
        Success(await vault.CreateAsync(
            new CreateSecretRequest(reference, "Credential", SecretKind.Token, Scope, management),
            material,
            default));

        Success(await vault.GetMetadataAsync(
            new GetSecretMetadataRequest(reference, Scope, management),
            default));
        AssertDenied(await vault.ResolveAsync(
            new ResolveSecretRequest(reference, Scope, management),
            default));
    }

    [Fact]
    public async Task Connection_environment_is_a_distinct_authorized_use_for_connection_secrets()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        var environmentPurpose = new SecretUsePurpose(
            SecretUseKind.ConnectionEnvironment,
            Scope.OwnerId!);
        using var material = SecretMaterial.CopyFrom([4, 2]);
        Success(await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "Connection environment",
                SecretKind.Token,
                Scope,
                environmentPurpose),
            material,
            default));

        using var resolved = Success(await vault.ResolveAsync(
            new ResolveSecretRequest(reference, Scope, environmentPurpose),
            default));

        Assert.Equal(2, resolved.Length);
    }

    [Fact]
    public async Task Database_authentication_is_authorized_only_for_database_scopes()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        var scope = new SecretScope(SecretScopeKind.DatabaseConnection, "database-prod");
        var purpose = new SecretUsePurpose(
            SecretUseKind.DatabaseConnectionAuthentication,
            "database-prod");
        using var material = SecretMaterial.CopyFrom([4, 2]);

        Success(await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "Database password",
                SecretKind.Password,
                scope,
                purpose),
            material,
            default));

        using var resolved = Success(await vault.ResolveAsync(
            new ResolveSecretRequest(reference, scope, purpose),
            default));
        Assert.Equal(2, resolved.Length);

        AssertDenied(await vault.ResolveAsync(
            new ResolveSecretRequest(
                reference,
                new SecretScope(SecretScopeKind.Connection, "database-prod"),
                purpose),
            default));
    }

    [Fact]
    public async Task Browser_authentication_is_authorized_only_for_the_exact_browser_profile_scope()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        var scope = new SecretScope(SecretScopeKind.BrowserProfile, "browser.internal");
        var purpose = new SecretUsePurpose(
            SecretUseKind.BrowserProfileAuthentication,
            "browser.internal");
        using var material = SecretMaterial.CopyFrom([4, 2]);

        Success(await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "Browser HTTP password",
                SecretKind.Password,
                scope,
                purpose),
            material,
            default));

        using var resolved = Success(await vault.ResolveAsync(
            new ResolveSecretRequest(reference, scope, purpose),
            default));
        Assert.Equal(2, resolved.Length);

        AssertDenied(await vault.ResolveAsync(
            new ResolveSecretRequest(
                reference,
                new SecretScope(SecretScopeKind.BrowserProfile, "browser.other"),
                new SecretUsePurpose(
                    SecretUseKind.BrowserProfileAuthentication,
                    "browser.other")),
            default));
        AssertDenied(await vault.ResolveAsync(
            new ResolveSecretRequest(
                reference,
                new SecretScope(SecretScopeKind.Connection, "browser.internal"),
                purpose),
            default));
    }

    [Fact]
    public async Task Create_denial_happens_before_an_adapter_reads_secret_material()
    {
        var wrongPurpose = new SecretUsePurpose(
            SecretUseKind.AiProviderAuthentication,
            Scope.OwnerId!);
        var disposed = SecretMaterial.CopyFrom([4, 2]);
        disposed.Dispose();
        ISecretVault[] vaults =
        [
            new InMemorySecretVault(),
            new UnavailableSecretVault("test", "Unavailable for testing."),
            PlatformSecretVaultFactory.Create(new SecretVaultFactoryOptions
            {
                AccessPolicy = SecretScopeAccessPolicy.Default,
            }).Vault,
        ];

        try
        {
            foreach (var vault in vaults)
            {
                AssertDenied(await vault.CreateAsync(
                    new CreateSecretRequest(
                        SecretRef.New(),
                        "Denied",
                        SecretKind.ApiKey,
                        Scope,
                        wrongPurpose),
                    disposed,
                    default));
            }
        }
        finally
        {
            foreach (var vault in vaults)
            {
                vault.Dispose();
            }
        }
    }

    [Fact]
    public async Task Listing_all_scopes_requires_the_explicit_all_secrets_target()
    {
        using var vault = new InMemorySecretVault();

        AssertDenied(await vault.ListMetadataAsync(
            new ListSecretMetadataRequest(
                null,
                new SecretUsePurpose(SecretUseKind.UserManagement, Scope.OwnerId!)),
            default));
        Success(await vault.ListMetadataAsync(
            new ListSecretMetadataRequest(null, SecretUsePurpose.ManageAll()),
            default));
    }

    private static T Success<T>(SecretVaultResult<T> result) =>
        Assert.IsType<SecretVaultResult<T>.Success>(result).Value;

    private static SecretVaultError Failure<T>(SecretVaultResult<T> result) =>
        Assert.IsType<SecretVaultResult<T>.Failure>(result).Error;

    private static void AssertDenied<T>(SecretVaultResult<T> result) =>
        Assert.Equal(SecretVaultErrorCode.AccessDenied, Failure(result).Code);
}
