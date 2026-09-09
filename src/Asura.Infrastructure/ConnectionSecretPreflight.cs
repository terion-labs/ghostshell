using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Proves that every opaque connection secret exists in the exact connection scope without
/// resolving its value. Material is resolved once, later, by the execution adapter or broker.
/// </summary>
internal sealed class ConnectionSecretPreflight(ISecretVault secretVault)
{
    public async ValueTask<ConnectionRuntimeResult<IReadOnlyList<ConnectionSecretRequirement>>> RunAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var requirements = BuildRequirements(profile);
        var scope = new SecretScope(SecretScopeKind.Connection, profile.Id.Value);
        foreach (var requirement in requirements)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ConnectionRuntimeResult<IReadOnlyList<ConnectionSecretRequirement>>.Fail(
                    ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Cancelled));
            }

            SecretVaultResult<SecretMetadata> metadata;
            try
            {
                metadata = await secretVault.GetMetadataAsync(
                        new GetSecretMetadataRequest(
                            requirement.Reference,
                            scope,
                            PurposeFor(requirement)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ConnectionRuntimeResult<IReadOnlyList<ConnectionSecretRequirement>>.Fail(
                    ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Cancelled));
            }

            if (metadata is SecretVaultResult<SecretMetadata>.Failure failure)
            {
                return ConnectionRuntimeResult<IReadOnlyList<ConnectionSecretRequirement>>.Fail(
                    MapError(failure.Error.Code));
            }
        }

        return ConnectionRuntimeResult<IReadOnlyList<ConnectionSecretRequirement>>.Succeed(
            requirements);

        SecretUsePurpose PurposeFor(ConnectionSecretRequirement requirement) => new(
            requirement.Role == ConnectionSecretRole.EnvironmentVariable
                ? SecretUseKind.ConnectionEnvironment
                : SecretUseKind.ConnectionAuthentication,
            profile.Id.Value);
    }

    private static IReadOnlyList<ConnectionSecretRequirement> BuildRequirements(
        ConnectionProfile profile)
    {
        var requirements = new List<ConnectionSecretRequirement>();
        switch (profile.Authentication)
        {
            case ConnectionAuthentication.Password password:
                requirements.Add(new ConnectionSecretRequirement(
                    ConnectionSecretRole.Password,
                    password.PasswordSecret));
                break;
            case ConnectionAuthentication.PrivateKey privateKey:
                requirements.Add(new ConnectionSecretRequirement(
                    ConnectionSecretRole.PrivateKey,
                    privateKey.PrivateKeySecret));
                if (privateKey.PassphraseSecret is { } passphrase)
                {
                    requirements.Add(new ConnectionSecretRequirement(
                        ConnectionSecretRole.PrivateKeyPassphrase,
                        passphrase));
                }

                break;
        }

        foreach (var variable in profile.Startup.Environment)
        {
            if (variable.Value is ConnectionEnvironmentValue.Secret secret)
            {
                requirements.Add(new ConnectionSecretRequirement(
                    ConnectionSecretRole.EnvironmentVariable,
                    secret.Reference,
                    variable.Name));
            }
        }

        return Array.AsReadOnly(requirements.ToArray());
    }

    private static ConnectionRuntimeError MapError(SecretVaultErrorCode code) =>
        ConnectionRuntimeError.Create(code switch
        {
            SecretVaultErrorCode.InvalidRequest => ConnectionRuntimeErrorCode.InvalidProfile,
            SecretVaultErrorCode.Unavailable => ConnectionRuntimeErrorCode.SecretVaultUnavailable,
            SecretVaultErrorCode.NotFound => ConnectionRuntimeErrorCode.SecretNotFound,
            SecretVaultErrorCode.AccessDenied => ConnectionRuntimeErrorCode.SecretAccessDenied,
            SecretVaultErrorCode.AuthenticationRequired => ConnectionRuntimeErrorCode.AuthenticationRequired,
            SecretVaultErrorCode.UserCancelled or SecretVaultErrorCode.Cancelled =>
                ConnectionRuntimeErrorCode.Cancelled,
            SecretVaultErrorCode.CorruptEntry => ConnectionRuntimeErrorCode.SecretInvalid,
            SecretVaultErrorCode.AlreadyExists or
                SecretVaultErrorCode.PlatformFailure or
                SecretVaultErrorCode.AuditPersistenceFailure =>
                ConnectionRuntimeErrorCode.SecretVaultFailure,
            _ => ConnectionRuntimeErrorCode.SecretVaultFailure,
        });
}
