using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>Immutable confidential targets; unchanged saves reuse their reference.</summary>
public sealed class DatabaseRecoveryState(ISecretVault vault, DatabaseRecoveryToken? restoredToken = null)
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    private DatabaseRecoveryToken _token = ValidateToken(restoredToken);
    private DatabaseRecoveryPayload? _saved;
    private long _requestedRevision;
    private bool _created = restoredToken is not null;

    public string? Target { get; private set; } = restoredToken?.Serialize();

    public bool CleanupIncomplete { get; private set; }

    public async Task<bool> SaveAsync(DatabaseRecoveryPayload payload, CancellationToken cancellationToken)
    {
        if (!vault.Availability.CanPersist)
        {
            return false;
        }
        var revision = Interlocked.Increment(ref _requestedRevision);
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (revision != Volatile.Read(ref _requestedRevision))
            {
                // A newer queued target owns this update; this is not a vault
                // failure. Target continues to name the last committed value.
                return true;
            }
            if (payload == _saved)
            {
                return true;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, DatabaseRecoveryJsonContext.Default.DatabaseRecoveryPayload);
            if (bytes.Length > SecretMaterial.MaximumLength)
            {
                CryptographicOperations.ZeroMemory(bytes);
                return false;
            }
            using var material = SecretMaterial.TakeOwnership(bytes);
            // An existing token may be held by a manually saved workspace or
            // another panel. Never change the value that token names.
            var candidate = _created ? new DatabaseRecoveryToken(_token.OwnerId, SecretRef.New()) : _token;
            var unpublished = true;
            try
            {
                var result = await vault.CreateAsync(new(candidate.Reference, "Database session recovery", SecretKind.Other,
                        Scope(candidate), Purpose(candidate)), material, cancellationToken).ConfigureAwait(true);
                if (result is not SecretVaultResult<SecretMetadata>.Success)
                {
                    // An ID collision is not evidence that we own the entry.
                    unpublished = result is not SecretVaultResult<SecretMetadata>.Failure { Error.Code: SecretVaultErrorCode.AlreadyExists };
                    return false;
                }
                cancellationToken.ThrowIfCancellationRequested();
                _created = true;
                _token = candidate;
                _saved = payload;
                Target = _token.Serialize();
                unpublished = false;
                return true;
            }
            finally
            {
                if (unpublished)
                {
                    await DeleteUnpublishedAsync(candidate).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<DatabaseRecoveryPayload?> RestoreAsync(CancellationToken cancellationToken)
    {
        var token = _token;
        var scope = Scope(token);
        var purpose = Purpose(token);
        var metadata = await vault.GetMetadataAsync(new(token.Reference, scope, purpose), cancellationToken).ConfigureAwait(false);
        if (metadata is not SecretVaultResult<SecretMetadata>.Success known
            || known.Value.Reference != token.Reference || known.Value.Scope != scope || known.Value.Kind != SecretKind.Other)
        {
            return null;
        }
        var resolved = await vault.ResolveAsync(new(token.Reference, scope, purpose), cancellationToken).ConfigureAwait(false);
        if (resolved is not SecretVaultResult<SecretMaterial>.Success success)
        {
            return null;
        }
        using var material = success.Value;
        var bytes = new byte[material.Length];
        try
        {
            material.CopyTo(bytes);
            var payload = JsonSerializer.Deserialize(bytes, DatabaseRecoveryJsonContext.Default.DatabaseRecoveryPayload);
            if (payload is null || string.IsNullOrWhiteSpace(payload.DriverId)
                || string.IsNullOrWhiteSpace(payload.ConnectionString))
            {
                return null;
            }
            _saved = payload;
            return payload;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static SecretScope Scope(DatabaseRecoveryToken token) => new(SecretScopeKind.DatabaseRecovery, token.OwnerId);

    private async Task DeleteUnpublishedAsync(DatabaseRecoveryToken token)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var result = await vault.DeleteAsync(new(token.Reference, Scope(token), Purpose(token)), cleanup.Token).ConfigureAwait(true);
            CleanupIncomplete |= result is SecretVaultResult<Unit>.Failure { Error.Code: not SecretVaultErrorCode.NotFound };
        }
        catch (Exception)
        {
            // Cleanup must not hide the original cancellation/storage failure.
            // No previously published token is ever eligible for this path.
            CleanupIncomplete = true;
        }
    }

    private static DatabaseRecoveryToken ValidateToken(DatabaseRecoveryToken? token) => token is null
        ? DatabaseRecoveryToken.Create()
        : DatabaseRecoveryToken.TryParse(token.Serialize())
            ?? throw new ArgumentException("The database recovery token is invalid.", nameof(token));

    private static SecretUsePurpose Purpose(DatabaseRecoveryToken token) => new(SecretUseKind.DatabaseRecovery, token.OwnerId);
}

public sealed record DatabaseRecoveryPayload(
    string DriverId,
    string ConnectionString,
    string? SessionPassword,
    ConnectionProfile? Tunnel,
    DatabaseConnectionProfile? CredentialOwner)
{
    internal bool MatchesSourceTarget(ScreenPanelDefinition source, string? recoveryTarget)
    {
        if (source.ConnectionId != Tunnel?.Id)
        {
            return false;
        }
        if (DatabasePanelTarget.TryParse(source.Startup.Location) is { } raw
            && string.Equals(raw.DriverId, DriverId, StringComparison.Ordinal)
            && string.Equals(raw.ConnectionString, ConnectionString, StringComparison.Ordinal))
        {
            return true;
        }
        if (CredentialOwner is { } profile
            && string.Equals(source.Startup.Location, "saved:" + profile.Id.Value, StringComparison.Ordinal)
            && string.Equals(profile.ConnectionString, ConnectionString, StringComparison.Ordinal)
            && string.Equals(profile.DriverId, DriverId, StringComparison.Ordinal))
        {
            return true;
        }
        return recoveryTarget is not null
            && string.Equals(source.Startup.Location, recoveryTarget, StringComparison.Ordinal);
    }
}

[JsonSerializable(typeof(DatabaseRecoveryPayload))]
internal sealed partial class DatabaseRecoveryJsonContext : JsonSerializerContext;
