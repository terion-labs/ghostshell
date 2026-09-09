using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

public sealed partial class WorkspaceNetworkRuntime
{
    private sealed partial class WorkspaceSession
    {
        // One retry per explicit rejection. Keep the saved profile unchanged: the
        // retry gets a password-free profile plus disposable session-only material.
        private async ValueTask<NetworkConnectionResult<WorkspaceNetworkSnapshot>> ApplyWithPasswordRecoveryAsync(
            WorkspaceNetworkPolicyUpdate update,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            var result = await ApplyAttemptAsync(update, progress, cancellationToken).ConfigureAwait(false);
            if (result is not NetworkConnectionResult<WorkspaceNetworkSnapshot>.Failure
                { Error.Code: NetworkConnectionErrorCode.AuthenticationRejected }
                || _passwordPrompt is null || !update.Policy.IsEnabled)
            {
                return result;
            }

            var selected = update.Connections.Single(connection => connection.Id == update.Policy.SelectedConnectionId);
            var reference = selected.Configuration switch
            {
                NetworkConnectionConfiguration.Proxy proxy => proxy.PasswordSecret,
                NetworkConnectionConfiguration.AnyConnect vpn => vpn.PasswordSecret,
                NetworkConnectionConfiguration.OpenVpn vpn => vpn.PasswordSecret,
                _ => null,
            };
            if (reference is null)
            {
                return result;
            }

            var prompt = new NetworkPasswordPromptRequest(selected.Id, selected.Name, storedPasswordRejected: true);
            var metadata = await PasswordMetadataAsync(selected.Id, reference.Value, cancellationToken).ConfigureAwait(false);
            progress?.Report(new NetworkConnectionProgress("Stored password rejected. Waiting for a replacement…"));
            var replacement = await _passwordPrompt.RequestPasswordAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (replacement is NetworkConnectionResult<SecretMaterial>.Failure failure)
            {
                var error = new NetworkConnectionError(failure.Error.Code, failure.Error.StableCode, failure.Error.Message, retryable: false);
                return _placement is WorkspaceNetworkPlacement.IsolatedPlacement
                    ? FailIsolatedGateway(selected.Id, error)
                    : Fail(selected.Id, error);
            }

            using var material = ((NetworkConnectionResult<SecretMaterial>.Success)replacement).Value;
            var configuration = selected.Configuration switch
            {
                NetworkConnectionConfiguration.Proxy proxy => (NetworkConnectionConfiguration)new NetworkConnectionConfiguration.Proxy(
                    proxy.Protocol, proxy.Host, proxy.Port, proxy.Username),
                NetworkConnectionConfiguration.AnyConnect vpn => new NetworkConnectionConfiguration.AnyConnect(
                    vpn.Gateway, vpn.Username, authenticationGroup: vpn.AuthenticationGroup, clientCertificateSecret: vpn.ClientCertificateSecret),
                NetworkConnectionConfiguration.OpenVpn vpn => new NetworkConnectionConfiguration.OpenVpn(vpn.ConfigurationSecret, vpn.Username),
                _ => throw new InvalidOperationException("The selected connection has no replaceable password."),
            };
            var retryProfile = new NetworkConnectionProfile(selected.Id, selected.SchemaVersion, selected.Name, configuration);
            var retryUpdate = new WorkspaceNetworkPolicyUpdate(update.Policy,
                [.. update.Connections.Select(connection => connection.Id == selected.Id ? retryProfile : connection)]);
            result = await ApplyAttemptAsync(retryUpdate, progress, cancellationToken, material).ConfigureAwait(false);
            if (result is NetworkConnectionResult<WorkspaceNetworkSnapshot>.Success { Value.State: WorkspaceNetworkState.Connected }
                && _secretVault is not null)
            {
                try
                {
                    await SaveReplacementPasswordAsync(prompt, reference.Value, metadata, material, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _ = await StopCurrentAsync().ConfigureAwait(false);
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A presentation failure must not discard ownership of an active route.
                    SecretSafeDiagnosticProjection.WriteTrace("network.password.update-prompt.failed", exception);
                }
            }

            return result;
        }

        private async ValueTask<SecretMetadata?> PasswordMetadataAsync(
            NetworkConnectionId connectionId, SecretRef reference, CancellationToken cancellationToken)
        {
            if (_secretVault is null)
            {
                return null;
            }

            try
            {
                var result = await _secretVault.GetMetadataAsync(new GetSecretMetadataRequest(reference,
                    new SecretScope(SecretScopeKind.NetworkConnection, connectionId.Value),
                    new SecretUsePurpose(SecretUseKind.UserManagement, connectionId.Value)), cancellationToken).ConfigureAwait(false);
                return result is SecretVaultResult<SecretMetadata>.Success success ? success.Value : null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                SecretSafeDiagnosticProjection.WriteTrace("network.password.metadata.failed", exception);
                return null;
            }
        }

        private async ValueTask SaveReplacementPasswordAsync(
            NetworkPasswordPromptRequest prompt, SecretRef reference, SecretMetadata? original,
            SecretMaterial material, CancellationToken cancellationToken)
        {
            if (!await _passwordPrompt!.ConfirmPasswordUpdateAsync(prompt, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var saved = false;
            try
            {
                var current = await PasswordMetadataAsync(prompt.ConnectionId, reference, cancellationToken).ConfigureAwait(false);
                // Do not overwrite a credential edited while the password dialog was open.
                if (original is not null && current?.UpdatedAt == original.UpdatedAt
                    && Snapshot.State == WorkspaceNetworkState.Connected)
                {
                    var result = await _secretVault!.ReplaceAsync(new ReplaceSecretRequest(reference,
                        new SecretScope(SecretScopeKind.NetworkConnection, prompt.ConnectionId.Value),
                        new SecretUsePurpose(SecretUseKind.UserManagement, prompt.ConnectionId.Value)), material, cancellationToken).ConfigureAwait(false);
                    saved = result is SecretVaultResult<SecretMetadata>.Success;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                SecretSafeDiagnosticProjection.WriteTrace("network.password.update.failed", exception);
            }

            if (!saved)
            {
                // Persistence failure must not tear down a successfully authenticated route.
                await _passwordPrompt.NotifyPasswordUpdateFailedAsync(prompt, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
