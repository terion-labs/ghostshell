using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class InMemorySecretVaultTests
{
    private static readonly SecretScope Scope = new(SecretScopeKind.Connection, "test-connection");
    private static readonly SecretUsePurpose Purpose = new(
        SecretUseKind.ConnectionAuthentication,
        "test-connection");

    [Fact]
    public async Task Full_lifecycle_is_memory_only_and_returns_independent_material()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        byte[] firstValue = [1, 3, 5, 7];
        using var firstMaterial = SecretMaterial.CopyFrom(firstValue);
        var createRequest = new CreateSecretRequest(
            reference,
            "Test credential",
            SecretKind.Token,
            Scope,
            Purpose);

        var created = Success(await vault.CreateAsync(createRequest, firstMaterial, default));

        Assert.Equal(SecretVaultPersistenceKind.MemoryOnly, created.Persistence);
        Assert.Equal(SecretVaultPersistenceKind.MemoryOnly, vault.Availability.Persistence);
        Assert.False(vault.Availability.CanPersist);
        Assert.Equal(SecretVaultCapabilities.All, vault.Availability.Capabilities);

        using (var resolved = Success(await vault.ResolveAsync(
                   new ResolveSecretRequest(reference, Scope, Purpose),
                   default)))
        {
            AssertSecret(firstValue, resolved);
        }

        var afterResolve = Success(await vault.GetMetadataAsync(
            new GetSecretMetadataRequest(reference, Scope, Purpose),
            default));
        Assert.NotNull(afterResolve.LastUsedAt);

        byte[] replacement = [2, 4, 6, 8];
        using var replacementMaterial = SecretMaterial.CopyFrom(replacement);
        var replaced = Success(await vault.ReplaceAsync(
            new ReplaceSecretRequest(reference, Scope, Purpose),
            replacementMaterial,
            default));
        Assert.True(replaced.UpdatedAt >= created.UpdatedAt);

        var relabelled = Success(await vault.RelabelAsync(
            new RelabelSecretRequest(reference, Scope, "Rotated credential", Purpose),
            default));
        Assert.Equal("Rotated credential", relabelled.Label);

        var listed = Success(await vault.ListMetadataAsync(
            new ListSecretMetadataRequest(Scope, Purpose),
            default));
        Assert.Equal(reference, Assert.Single(listed).Reference);

        using (var resolved = Success(await vault.ResolveAsync(
                   new ResolveSecretRequest(reference, Scope, Purpose),
                   default)))
        {
            AssertSecret(replacement, resolved);
        }

        Success(await vault.DeleteAsync(new DeleteSecretRequest(reference, Scope, Purpose), default));
        Failure(
            await vault.ResolveAsync(new ResolveSecretRequest(reference, Scope, Purpose), default),
            SecretVaultErrorCode.NotFound);
    }

    [Fact]
    public async Task Duplicate_create_and_cancelled_operations_are_typed_failures()
    {
        using var vault = new InMemorySecretVault();
        var reference = SecretRef.New();
        using var material = SecretMaterial.CopyFrom([9]);
        var request = new CreateSecretRequest(
            reference,
            "Credential",
            SecretKind.Password,
            Scope,
            Purpose);
        Success(await vault.CreateAsync(request, material, default));

        Failure(
            await vault.CreateAsync(request, material, default),
            SecretVaultErrorCode.AlreadyExists);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Failure(
            await vault.GetMetadataAsync(
                new GetSecretMetadataRequest(reference, Scope, Purpose),
                cancellation.Token),
            SecretVaultErrorCode.Cancelled);
    }

    private static void AssertSecret(byte[] expected, SecretMaterial material)
    {
        var copy = new byte[material.Length];

        try
        {
            material.CopyTo(copy);
            Assert.Equal(expected, copy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static T Success<T>(SecretVaultResult<T> result) =>
        Assert.IsType<SecretVaultResult<T>.Success>(result).Value;

    private static void Failure<T>(SecretVaultResult<T> result, SecretVaultErrorCode expected) =>
        Assert.Equal(expected, Assert.IsType<SecretVaultResult<T>.Failure>(result).Error.Code);
}
