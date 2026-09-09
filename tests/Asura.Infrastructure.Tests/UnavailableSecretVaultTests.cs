using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class UnavailableSecretVaultTests
{
    private static readonly SecretScope Scope = new(SecretScopeKind.Connection, "test-connection");
    private static readonly SecretUsePurpose Purpose = new(
        SecretUseKind.ConnectionAuthentication,
        "test-connection");

    [Fact]
    public async Task Every_operation_fails_closed_without_consuming_secret_material()
    {
        using var vault = new UnavailableSecretVault("test_unavailable", "Unavailable for testing.");
        var reference = SecretRef.New();
        using var material = SecretMaterial.CopyFrom([1, 2, 3]);
        var create = new CreateSecretRequest(
            reference,
            "Credential",
            SecretKind.ApiKey,
            Scope,
            Purpose);

        Assert.Equal(SecretVaultPersistenceKind.None, vault.Availability.Persistence);
        Assert.Equal(SecretVaultCapabilities.None, vault.Availability.Capabilities);
        Assert.False(vault.Availability.CanPersist);

        Failure(await vault.CreateAsync(create, material, default), SecretVaultErrorCode.Unavailable);
        Failure(
            await vault.ResolveAsync(new ResolveSecretRequest(reference, Scope, Purpose), default),
            SecretVaultErrorCode.Unavailable);
        Failure(
            await vault.ReplaceAsync(new ReplaceSecretRequest(reference, Scope, Purpose), material, default),
            SecretVaultErrorCode.Unavailable);
        Failure(
            await vault.RelabelAsync(
                new RelabelSecretRequest(reference, Scope, "New label", Purpose),
                default),
            SecretVaultErrorCode.Unavailable);
        Failure(
            await vault.DeleteAsync(new DeleteSecretRequest(reference, Scope, Purpose), default),
            SecretVaultErrorCode.Unavailable);
        Failure(
            await vault.GetMetadataAsync(
                new GetSecretMetadataRequest(reference, Scope, Purpose),
                default),
            SecretVaultErrorCode.Unavailable);
        Failure(
            await vault.ListMetadataAsync(
                new ListSecretMetadataRequest(null, SecretUsePurpose.ManageAll()),
                default),
            SecretVaultErrorCode.Unavailable);

        Assert.Equal(3, material.Length);
    }

    [Fact]
    public async Task Cancellation_takes_precedence_over_unavailability()
    {
        using var vault = new UnavailableSecretVault("test_unavailable", "Unavailable for testing.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Failure(
            await vault.ResolveAsync(
                new ResolveSecretRequest(SecretRef.New(), Scope, Purpose),
                cancellation.Token),
            SecretVaultErrorCode.Cancelled);
    }

    private static void Failure<T>(SecretVaultResult<T> result, SecretVaultErrorCode expected) =>
        Assert.Equal(expected, Assert.IsType<SecretVaultResult<T>.Failure>(result).Error.Code);
}
