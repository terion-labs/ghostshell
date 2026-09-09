using System.Security.Cryptography;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Providers;

/// <summary>
/// Persists refreshable OAuth sessions exclusively as bounded vault material.
/// The JSON representation exists only in zeroed request-local buffers.
/// </summary>
internal sealed class AiProviderOAuthVault(ISecretVault vault)
{
    private const int MaximumSessionBytes = 256 * 1024;

    public async ValueTask StoreAsync(
        AiProviderProfileId profileId,
        SecretRef sessionReference,
        AiProviderOAuthSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            session,
            AgentProviderJsonContext.Default.AiProviderOAuthSession);
        try
        {
            if (bytes.Length > MaximumSessionBytes)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.CredentialUnavailable);
            }

            using var material = SecretMaterial.TakeOwnership(bytes);
            bytes = [];
            var scope = Scope(profileId);
            var purpose = Purpose(profileId);
            var created = await vault.CreateAsync(
                new CreateSecretRequest(
                    sessionReference,
                    "AI provider OAuth session",
                    SecretKind.Token,
                    scope,
                    purpose),
                material,
                cancellationToken).ConfigureAwait(false);
            if (created is SecretVaultResult<SecretMetadata>.Success)
            {
                return;
            }

            var failure = ((SecretVaultResult<SecretMetadata>.Failure)created).Error;
            if (failure.Code != SecretVaultErrorCode.AlreadyExists)
            {
                throw MapFailure(failure.Code);
            }

            var replaced = await vault.ReplaceAsync(
                new ReplaceSecretRequest(sessionReference, scope, purpose),
                material,
                cancellationToken).ConfigureAwait(false);
            if (replaced is SecretVaultResult<SecretMetadata>.Failure replaceFailure)
            {
                throw MapFailure(replaceFailure.Error.Code);
            }
        }
        finally
        {
            if (bytes.Length > 0)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    public async ValueTask<AiProviderOAuthSession> ResolveAsync(
        AiProviderProfileId profileId,
        SecretRef sessionReference,
        CancellationToken cancellationToken)
    {
        var resolved = await vault.ResolveAsync(
            new ResolveSecretRequest(
                sessionReference,
                Scope(profileId),
                Purpose(profileId)),
            cancellationToken).ConfigureAwait(false);
        if (resolved is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            throw MapFailure(failure.Error.Code);
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)resolved).Value;
        if (material.Length > MaximumSessionBytes)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.CredentialUnavailable);
        }

        var bytes = new byte[material.Length];
        try
        {
            material.CopyTo(bytes);
            return JsonSerializer.Deserialize(
                    bytes,
                    AgentProviderJsonContext.Default.AiProviderOAuthSession)
                ?? throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.CredentialUnavailable);
        }
        catch (JsonException exception)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.CredentialUnavailable,
                innerException: exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static SecretScope Scope(AiProviderProfileId profileId) =>
        new(SecretScopeKind.AiProvider, profileId.Value);

    private static SecretUsePurpose Purpose(AiProviderProfileId profileId) =>
        new(SecretUseKind.AiProviderAuthentication, profileId.Value);

    private static AiProviderClientException MapFailure(SecretVaultErrorCode code) =>
        code is SecretVaultErrorCode.Cancelled or SecretVaultErrorCode.UserCancelled
            ? AiProviderClientException.Create(AiProviderRuntimeErrorCode.Cancelled)
            : AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.CredentialUnavailable);
}
