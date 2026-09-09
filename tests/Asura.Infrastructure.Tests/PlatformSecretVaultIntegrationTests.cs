using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class PlatformSecretVaultIntegrationTests
{
    [NativeVaultIntegrationFact]
    public async Task Native_vault_round_trips_only_when_explicitly_enabled()
    {
        var acceptanceRun = NativeVaultAcceptanceRun.FromEnvironment();
        var runId = acceptanceRun?.RunId ?? Guid.NewGuid().ToString("N");
        var dataDirectory = acceptanceRun?.DataDirectory
            ?? Directory.CreateTempSubdirectory("asura-vault-integration-");
        Directory.CreateDirectory(dataDirectory.FullName);
        var serviceName = $"sh.asura.integration-tests.{runId}";
        var preserveRecoveryMetadata = false;
        var reference = acceptanceRun?.Reference ?? SecretRef.New();
        var purpose = new SecretUsePurpose(
            SecretUseKind.PlatformMaintenance,
            SecretUsePurpose.GlobalTargetId);
        var wrongPurpose = new SecretUsePurpose(
            SecretUseKind.AiProviderAuthentication,
            SecretUsePurpose.GlobalTargetId);
        byte[] initialBytes = [0x47, 0x68, 0x6f, 0x73, 0x74];
        byte[] replacementBytes = [0x53, 0x68, 0x65, 0x6c, 0x6c];
        var request = new CreateSecretRequest(
            reference,
            $"Asura isolated integration test {reference.Value}",
            SecretKind.Token,
            SecretScope.Global,
            purpose);
        acceptanceRun?.Record("INITIALIZED");

        try
        {
            var selection = PlatformSecretVaultFactory.Create(new SecretVaultFactoryOptions
            {
                ServiceName = serviceName,
                DataDirectory = dataDirectory.FullName,
            });
            using var vault = selection.Vault;
            Assert.True(vault.Availability.CanPersist, selection.Diagnostic.Message);
            Assert.Equal(
                SecretVaultPersistenceKind.OsProtectedPersistent,
                vault.Availability.Persistence);
            Assert.Equal(SecretVaultCapabilities.All, vault.Availability.Capabilities);

            var created = false;
            try
            {
                using var input = SecretMaterial.CopyFrom(initialBytes);
                var metadata = Success(await vault.CreateAsync(request, input, default));
                created = true;
                acceptanceRun?.Record("CREATED");
                Assert.Equal(reference, metadata.Reference);
                Assert.Equal(SecretVaultPersistenceKind.OsProtectedPersistent, metadata.Persistence);

                Failure(
                    await vault.CreateAsync(request, input, default),
                    SecretVaultErrorCode.AlreadyExists);
                Failure(
                    await vault.ResolveAsync(
                        new ResolveSecretRequest(reference, SecretScope.Global, wrongPurpose),
                        default),
                    SecretVaultErrorCode.AccessDenied);

                var beforeResolve = Success(await vault.GetMetadataAsync(
                    new GetSecretMetadataRequest(reference, SecretScope.Global, purpose),
                    default));
                Assert.Null(beforeResolve.LastUsedAt);

                var listed = Success(await vault.ListMetadataAsync(
                    new ListSecretMetadataRequest(SecretScope.Global, purpose),
                    default));
                Assert.Equal(reference, Assert.Single(listed).Reference);

                using (var resolved = Success(await vault.ResolveAsync(
                           new ResolveSecretRequest(reference, SecretScope.Global, purpose),
                           default)))
                {
                    AssertSecret(initialBytes, resolved);
                }

                var afterResolve = Success(await vault.GetMetadataAsync(
                    new GetSecretMetadataRequest(reference, SecretScope.Global, purpose),
                    default));
                Assert.NotNull(afterResolve.LastUsedAt);

                using var replacement = SecretMaterial.CopyFrom(replacementBytes);
                var replaced = Success(await vault.ReplaceAsync(
                    new ReplaceSecretRequest(reference, SecretScope.Global, purpose),
                    replacement,
                    default));
                Assert.True(replaced.UpdatedAt >= metadata.UpdatedAt);

                using (var resolved = Success(await vault.ResolveAsync(
                           new ResolveSecretRequest(reference, SecretScope.Global, purpose),
                           default)))
                {
                    AssertSecret(replacementBytes, resolved);
                }

                const string relabelled = "Asura isolated integration test (relabelled)";
                var relabelledMetadata = Success(await vault.RelabelAsync(
                    new RelabelSecretRequest(
                        reference,
                        SecretScope.Global,
                        relabelled,
                        purpose),
                    default));
                Assert.Equal(relabelled, relabelledMetadata.Label);

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                Failure(
                    await vault.GetMetadataAsync(
                        new GetSecretMetadataRequest(reference, SecretScope.Global, purpose),
                        cancellation.Token),
                    SecretVaultErrorCode.Cancelled);

                Success(await vault.DeleteAsync(
                    new DeleteSecretRequest(reference, SecretScope.Global, purpose),
                    default));
                created = false;
                acceptanceRun?.Record("DELETED");
                Failure(
                    await vault.ResolveAsync(
                        new ResolveSecretRequest(reference, SecretScope.Global, purpose),
                        default),
                    SecretVaultErrorCode.NotFound);
                Assert.Empty(Success(await vault.ListMetadataAsync(
                    new ListSecretMetadataRequest(SecretScope.Global, purpose),
                    default)));
            }
            finally
            {
                if (created)
                {
                    preserveRecoveryMetadata = true;
                    SecretVaultResult<Unit> cleanup;
                    try
                    {
                        cleanup = await vault.DeleteAsync(
                            new DeleteSecretRequest(reference, SecretScope.Global, purpose),
                            default);
                    }
                    catch (Exception exception)
                    {
                        acceptanceRun?.Record("CLEANUP_FAILED");
                        throw new InvalidOperationException(
                            $"Native vault cleanup threw for synthetic service '{serviceName}', " +
                            $"reference '{reference.Value}', metadata directory '{dataDirectory.FullName}'. " +
                            "Remove that isolated test entry manually.",
                            exception);
                    }

                    if (cleanup is SecretVaultResult<Unit>.Failure failure)
                    {
                        acceptanceRun?.Record("CLEANUP_FAILED");
                        throw new InvalidOperationException(
                            $"Native vault cleanup failed for synthetic service '{serviceName}', " +
                            $"reference '{reference.Value}', metadata directory '{dataDirectory.FullName}', " +
                            $"with code '{failure.Error.StableCode}'. Remove that isolated test entry manually.");
                    }

                    preserveRecoveryMetadata = false;
                    acceptanceRun?.Record("DELETED");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(initialBytes);
            CryptographicOperations.ZeroMemory(replacementBytes);
            if (!preserveRecoveryMetadata && Directory.Exists(dataDirectory.FullName))
            {
                dataDirectory.Delete(recursive: true);
            }
        }
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

    private static void Failure<T>(
        SecretVaultResult<T> result,
        SecretVaultErrorCode expected) =>
        Assert.Equal(expected, Assert.IsType<SecretVaultResult<T>.Failure>(result).Error.Code);
}

internal sealed class NativeVaultIntegrationFactAttribute : FactAttribute
{
    public NativeVaultIntegrationFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ASURA_RUN_SECRET_VAULT_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set ASURA_RUN_SECRET_VAULT_INTEGRATION=1 to exercise the current user's native credential vault.";
        }
    }
}
