using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns database-profile persistence and the credential lifecycle coupled to
/// it. Runtime panel binding remains a shell composition concern.
/// </summary>
public sealed class DatabaseConnectionSettingsCoordinator
{
    private readonly IDefinitionCatalog _catalog;
    private readonly IDatabaseConnectionCatalog? _databaseConnectionCatalog;
    private readonly ISecretVault _secretVault;
    private readonly Action<string> _setError;
    private readonly Action<string> _setVaultStatus;

    public DatabaseConnectionSettingsCoordinator(
        IDefinitionCatalog catalog,
        IDatabaseConnectionCatalog? databaseConnectionCatalog,
        ISecretVault secretVault,
        Action<string> setError,
        Action<string> setVaultStatus)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _databaseConnectionCatalog = databaseConnectionCatalog;
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _setError = setError ?? throw new ArgumentNullException(nameof(setError));
        _setVaultStatus = setVaultStatus
            ?? throw new ArgumentNullException(nameof(setVaultStatus));
    }

    /// <summary>
    /// Persists a database connection, optionally moving the typed password
    /// into the OS vault. The stored connection string never carries the
    /// password. Updates keep the existing profile id and its stored secret
    /// unless a new password is being stored.
    /// </summary>
    public async Task<DatabaseConnectionProfile?> SaveDatabaseConnectionAsync(
        DatabaseConnectionProfileId? existingId,
        string name,
        string driverId,
        DatabaseConnectionDetails details,
        bool storePassword,
        ConnectionId? tunnelConnectionId,
        DatabaseInlineTunnelRequest? inlineTunnel = null,
        CancellationToken cancellationToken = default,
        DatabaseConnectionProfileId? draftId = null)
    {
        if (_databaseConnectionCatalog is null || string.IsNullOrWhiteSpace(name))
        {
            _setError("A saved database connection needs a name.");
            return null;
        }

        var existing = existingId is { } id
            ? _catalog.Snapshot.DatabaseConnections
                .SingleOrDefault(item => item.Value.Id == id)
            : null;
        if (existingId is not null && existing is null)
        {
            _setError("That database connection no longer exists.");
            return null;
        }
        // Draft identity is distinct from edit intent: a create still uses a
        // null expected revision and cannot overwrite a concurrent definition.
        var profileId = existing?.Value.Id ?? draftId ?? DatabaseConnectionProfileId.New();
        if (existingId is null && _catalog.Snapshot.DatabaseConnections.Any(item => item.Value.Id == profileId))
        {
            _setError("That database draft was already saved. Reopen it before editing.");
            return null;
        }
        var secret = existing?.Value.PasswordSecret;
        if (storePassword && !string.IsNullOrEmpty(details.Password))
        {
            var reference = SecretRef.New();
            var bytes = Encoding.UTF8.GetBytes(details.Password);
            using var material = SecretMaterial.TakeOwnership(bytes);
            var created = await _secretVault.CreateAsync(
                new CreateSecretRequest(
                    reference,
                    $"{name.Trim()} database password",
                    SecretKind.Password,
                    new SecretScope(SecretScopeKind.DatabaseConnection, profileId.Value),
                    new SecretUsePurpose(
                        SecretUseKind.DatabaseConnectionAuthentication,
                        profileId.Value)),
                material,
                cancellationToken);
            if (created is SecretVaultResult<SecretMetadata>.Failure failure)
            {
                _setError(failure.Error.Message);
                return null;
            }

            secret = reference;
        }

        ConnectionProfile? inline = null;
        if (inlineTunnel is { } tunnelRequest)
        {
            inline = await BuildInlineTunnelAsync(
                profileId,
                name.Trim(),
                tunnelRequest,
                existing?.Value.InlineTunnel,
                cancellationToken);
            if (inline is null)
            {
                return null;
            }
        }

        var profile = new DatabaseConnectionProfile(
            profileId,
            DatabaseConnectionProfile.CurrentSchemaVersion,
            name.Trim(),
            driverId,
            _databaseConnectionCatalog.BuildConnectionString(
                driverId,
                details with { Password = null }),
            secret,
            inline is null ? tunnelConnectionId : null,
            inline);
        var saved = await _catalog.SaveDatabaseConnectionAsync(
            profile,
            existing?.Revision,
            cancellationToken);
        if (!saved.IsSuccess)
        {
            _setError(saved.Error!.Message);
            return null;
        }

        return saved.Value!.Value;
    }

    /// <summary>
    /// Adds a password supplied by the connection-time prompt to an existing
    /// database profile. If the catalog update fails, the newly-created,
    /// unreferenced vault entry is removed before returning.
    /// </summary>
    public async Task<DatabaseConnectionProfile?> StoreDatabasePasswordAsync(
        DatabaseConnectionProfileId profileId,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!_secretVault.Availability.CanPersist)
        {
            _setError(_secretVault.Availability.Message);
            return null;
        }

        if (string.IsNullOrEmpty(password))
        {
            _setError("Enter a password before saving it.");
            return null;
        }

        var stored = _catalog.Snapshot.DatabaseConnections
            .SingleOrDefault(item => item.Value.Id == profileId);
        if (stored is null)
        {
            _setError("That database connection no longer exists.");
            return null;
        }

        if (stored.Value.PasswordSecret is not null)
        {
            return stored.Value;
        }

        var reference = SecretRef.New();
        var scope = new SecretScope(SecretScopeKind.DatabaseConnection, profileId.Value);
        var purpose = new SecretUsePurpose(
            SecretUseKind.DatabaseConnectionAuthentication,
            profileId.Value);
        var bytes = Encoding.UTF8.GetBytes(password);
        using var material = SecretMaterial.TakeOwnership(bytes);
        var created = await _secretVault.CreateAsync(
            new CreateSecretRequest(
                reference,
                $"{stored.Value.Name} database password",
                SecretKind.Password,
                scope,
                purpose),
            material,
            cancellationToken);
        if (created is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            _setError(failure.Error.Message);
            return null;
        }

        var profile = new DatabaseConnectionProfile(
            stored.Value.Id,
            stored.Value.SchemaVersion,
            stored.Value.Name,
            stored.Value.DriverId,
            stored.Value.ConnectionString,
            reference,
            stored.Value.TunnelConnectionId,
            stored.Value.InlineTunnel);
        DefinitionStoreResult<StoredDefinition<DatabaseConnectionProfile>> saved;
        try
        {
            saved = await _catalog.SaveDatabaseConnectionAsync(
                profile,
                stored.Revision,
                cancellationToken);
        }
        catch
        {
            await DeleteUnusedDatabasePasswordAsync(reference, scope, profileId);
            throw;
        }

        if (!saved.IsSuccess)
        {
            await DeleteUnusedDatabasePasswordAsync(reference, scope, profileId);
            _setError(saved.Error!.Message);
            return null;
        }

        return saved.Value!.Value;
    }

    private async Task DeleteUnusedDatabasePasswordAsync(
        SecretRef reference,
        SecretScope scope,
        DatabaseConnectionProfileId profileId)
    {
        var deleted = await _secretVault.DeleteAsync(
            new DeleteSecretRequest(
                reference,
                scope,
                new SecretUsePurpose(SecretUseKind.UserManagement, profileId.Value)),
            CancellationToken.None);
        if (deleted is SecretVaultResult<Unit>.Failure)
        {
            _setVaultStatus(
                "An unused database credential could not be removed from the system credential store.");
        }
    }

    /// <summary>
    /// Turns the editor's inline-tunnel request into the profile stored inside
    /// the database connection. The tunnel's id derives from the database
    /// profile so its keychain password stays scoped and resolvable across
    /// edits; a request with no new password keeps the stored one.
    /// </summary>
    private async Task<ConnectionProfile?> BuildInlineTunnelAsync(
        DatabaseConnectionProfileId profileId,
        string name,
        DatabaseInlineTunnelRequest request,
        ConnectionProfile? existingInline,
        CancellationToken cancellationToken)
    {
        var tunnelId = DatabaseConnectionProfile.InlineTunnelId(profileId);
        ConnectionAuthentication authentication;
        if (request.UseAgent)
        {
            authentication = new ConnectionAuthentication.SshAgent();
        }
        else if (!string.IsNullOrEmpty(request.Password))
        {
            var reference = SecretRef.New();
            var bytes = Encoding.UTF8.GetBytes(request.Password);
            using var material = SecretMaterial.TakeOwnership(bytes);
            var created = await _secretVault.CreateAsync(
                new CreateSecretRequest(
                    reference,
                    $"{name} tunnel password",
                    SecretKind.Password,
                    new SecretScope(SecretScopeKind.Connection, tunnelId.Value),
                    new SecretUsePurpose(
                        SecretUseKind.ConnectionAuthentication,
                        tunnelId.Value)),
                material,
                cancellationToken);
            if (created is SecretVaultResult<SecretMetadata>.Failure failure)
            {
                _setError(failure.Error.Message);
                return null;
            }

            authentication = new ConnectionAuthentication.Password(reference);
        }
        else if (existingInline?.Authentication is ConnectionAuthentication.Password kept)
        {
            authentication = kept;
        }
        else
        {
            _setError("The SSH tunnel needs a password, or switch it to the SSH agent.");
            return null;
        }

        return DatabaseConnectionEditorViewModel.BuildInlineTunnelProfile(
            tunnelId,
            $"{name} tunnel",
            request,
            authentication);
    }

    /// <summary>Resolves a stored database password from the OS vault.</summary>
    public async Task<string?> ResolveDatabasePasswordAsync(
        DatabaseConnectionProfile requestedProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedProfile);
        var owner = _catalog.Snapshot.DatabaseConnections
            .SingleOrDefault(item => item.Value.Id == requestedProfile.Id)?.Value;
        if (owner is null || owner.PasswordSecret is not { } secret
            || owner.PasswordSecret != requestedProfile.PasswordSecret
            || !string.Equals(owner.DriverId, requestedProfile.DriverId, StringComparison.Ordinal)
            || !string.Equals(owner.ConnectionString, requestedProfile.ConnectionString, StringComparison.Ordinal)
            || owner.TunnelConnectionId != requestedProfile.TunnelConnectionId
            || owner.InlineTunnel != requestedProfile.InlineTunnel)
        {
            return null;
        }

        var result = await _secretVault.ResolveAsync(
            new ResolveSecretRequest(
                secret,
                new SecretScope(SecretScopeKind.DatabaseConnection, owner.Id.Value),
                new SecretUsePurpose(
                    SecretUseKind.DatabaseConnectionAuthentication,
                    owner.Id.Value)),
            cancellationToken);
        if (result is SecretVaultResult<SecretMaterial>.Failure)
        {
            return null;
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
