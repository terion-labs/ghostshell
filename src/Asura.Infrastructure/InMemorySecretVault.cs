using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Process-local vault for explicitly temporary credentials. It never claims persistence.
/// </summary>
public sealed class InMemorySecretVault : ISecretVault
{
    private readonly object _gate = new();
    private readonly Dictionary<SecretRef, Entry> _entries = [];
    private readonly TimeProvider _timeProvider;
    private readonly ISecretAccessPolicy _accessPolicy;
    private bool _disposed;

    public InMemorySecretVault(
        TimeProvider? timeProvider = null,
        ISecretAccessPolicy? accessPolicy = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _accessPolicy = accessPolicy ?? SecretScopeAccessPolicy.Default;
        Availability = new SecretVaultAvailability(
            SecretVaultAvailabilityState.Available,
            SecretVaultPersistenceKind.MemoryOnly,
            SecretVaultCapabilities.All,
            "memory",
            "memory_only",
            "Credentials are available only for the current process and will not survive restart.");
    }

    public SecretVaultAvailability Availability { get; }

    public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
        CreateSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<SecretMetadata>(
            _accessPolicy,
            SecretVaultOperation.Create,
            request.Scope,
            request.Purpose);
        if (denied is not null)
        {
            return ValueTask.FromResult(denied);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultFailures.Cancelled<SecretMetadata>());
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.ContainsKey(request.Reference))
            {
                return ValueTask.FromResult(SecretVaultFailures.AlreadyExists<SecretMetadata>());
            }

            var value = SecretVaultBuffers.Copy(material);
            var now = _timeProvider.GetUtcNow();
            var metadata = new SecretMetadata(
                request.Reference,
                request.Label,
                request.Kind,
                request.Scope,
                SecretVaultPersistenceKind.MemoryOnly,
                now,
                now);

            _entries.Add(request.Reference, new Entry(metadata, value));
            return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Succeed(metadata));
        }
    }

    public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<SecretMaterial>(
            _accessPolicy,
            SecretVaultOperation.Resolve,
            request.Scope,
            request.Purpose);
        if (denied is not null)
        {
            return ValueTask.FromResult(denied);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultFailures.Cancelled<SecretMaterial>());
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(request.Reference, out var entry))
            {
                return ValueTask.FromResult(SecretVaultFailures.NotFound<SecretMaterial>());
            }

            var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<SecretMaterial>(
                request.Scope,
                entry.Metadata.Scope);
            if (scopeMismatch is not null)
            {
                return ValueTask.FromResult(scopeMismatch);
            }

            var metadata = entry.Metadata with { LastUsedAt = _timeProvider.GetUtcNow() };
            _entries[request.Reference] = entry with { Metadata = metadata };
            var material = SecretMaterial.TakeOwnership([.. entry.Value]);
            return ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Succeed(material));
        }
    }

    public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
        ReplaceSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<SecretMetadata>(
            _accessPolicy,
            SecretVaultOperation.Replace,
            request.Scope,
            request.Purpose);
        if (denied is not null)
        {
            return ValueTask.FromResult(denied);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultFailures.Cancelled<SecretMetadata>());
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(request.Reference, out var entry))
            {
                return ValueTask.FromResult(SecretVaultFailures.NotFound<SecretMetadata>());
            }

            var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<SecretMetadata>(
                request.Scope,
                entry.Metadata.Scope);
            if (scopeMismatch is not null)
            {
                return ValueTask.FromResult(scopeMismatch);
            }

            var replacement = SecretVaultBuffers.Copy(material);
            CryptographicOperations.ZeroMemory(entry.Value);
            var metadata = entry.Metadata with { UpdatedAt = _timeProvider.GetUtcNow() };
            _entries[request.Reference] = new Entry(metadata, replacement);
            return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Succeed(metadata));
        }
    }

    public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
        RelabelSecretRequest request,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<SecretMetadata>(
            _accessPolicy,
            SecretVaultOperation.Relabel,
            request.Scope,
            request.Purpose);
        if (denied is not null)
        {
            return ValueTask.FromResult(denied);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultFailures.Cancelled<SecretMetadata>());
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(request.Reference, out var entry))
            {
                return ValueTask.FromResult(SecretVaultFailures.NotFound<SecretMetadata>());
            }

            var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<SecretMetadata>(
                request.Scope,
                entry.Metadata.Scope);
            if (scopeMismatch is not null)
            {
                return ValueTask.FromResult(scopeMismatch);
            }

            var metadata = entry.Metadata with
            {
                Label = request.Label,
                UpdatedAt = _timeProvider.GetUtcNow(),
            };
            _entries[request.Reference] = entry with { Metadata = metadata };
            return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Succeed(metadata));
        }
    }

    public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
        DeleteSecretRequest request,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<Unit>(
            _accessPolicy,
            SecretVaultOperation.Delete,
            request.Scope,
            request.Purpose);
        if (denied is not null)
        {
            return ValueTask.FromResult(denied);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultFailures.Cancelled<Unit>());
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(request.Reference, out var entry))
            {
                return ValueTask.FromResult(SecretVaultFailures.NotFound<Unit>());
            }

            var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<Unit>(
                request.Scope,
                entry.Metadata.Scope);
            if (scopeMismatch is not null)
            {
                return ValueTask.FromResult(scopeMismatch);
            }

            _entries.Remove(request.Reference);

            CryptographicOperations.ZeroMemory(entry.Value);
            return ValueTask.FromResult(SecretVaultResult<Unit>.Succeed(Unit.Value));
        }
    }

    public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
        GetSecretMetadataRequest request,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<SecretMetadata>(
            _accessPolicy,
            SecretVaultOperation.GetMetadata,
            request.Scope,
            request.Purpose);
        if (denied is not null)
        {
            return ValueTask.FromResult(denied);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultFailures.Cancelled<SecretMetadata>());
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(request.Reference, out var entry))
            {
                return ValueTask.FromResult(SecretVaultFailures.NotFound<SecretMetadata>());
            }

            var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<SecretMetadata>(
                request.Scope,
                entry.Metadata.Scope);
            return ValueTask.FromResult(
                scopeMismatch ?? SecretVaultResult<SecretMetadata>.Succeed(entry.Metadata));
        }
    }

    public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
        ListSecretMetadataRequest request,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<IReadOnlyList<SecretMetadata>>(
            _accessPolicy,
            SecretVaultOperation.ListMetadata,
            request.Scope,
            request.Purpose);
        if (denied is not null)
        {
            return ValueTask.FromResult(denied);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultFailures.Cancelled<IReadOnlyList<SecretMetadata>>());
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var metadata = _entries.Values
                .Select(entry => entry.Metadata)
                .Where(item => request.Scope is null || item.Scope == request.Scope)
                .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Reference.Value, StringComparer.Ordinal)
                .ToArray();
            return ValueTask.FromResult(
                SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed(metadata));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var entry in _entries.Values)
            {
                CryptographicOperations.ZeroMemory(entry.Value);
            }

            _entries.Clear();
            _disposed = true;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record Entry(SecretMetadata Metadata, byte[] Value);
}
