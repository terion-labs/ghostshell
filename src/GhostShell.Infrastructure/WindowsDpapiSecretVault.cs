using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Stores one current-user DPAPI ciphertext per secret reference. Files contain metadata and ciphertext only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsDpapiSecretVault : ISecretVault
{
    private const int FormatVersion = 1;
    private const string FileExtension = ".gsvault";

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly byte[] _optionalEntropy;
    private readonly ISecretAccessPolicy _accessPolicy;
    private bool _disposed;

    public WindowsDpapiSecretVault(
        string directory,
        string serviceName = ApplicationStorageIdentity.SecretServiceName,
        ISecretAccessPolicy? accessPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        _directory = Path.GetFullPath(directory);
        _optionalEntropy = SHA256.HashData(Encoding.UTF8.GetBytes(serviceName));
        _accessPolicy = accessPolicy ?? SecretScopeAccessPolicy.Default;

        Availability = OperatingSystem.IsWindows()
            ? new SecretVaultAvailability(
                SecretVaultAvailabilityState.Available,
                SecretVaultPersistenceKind.OsProtectedPersistent,
                SecretVaultCapabilities.All,
                "windows-dpapi",
                "windows_dpapi_current_user",
                "Credentials are protected for the current Windows user with DPAPI.")
            : new SecretVaultAvailability(
                SecretVaultAvailabilityState.Unavailable,
                SecretVaultPersistenceKind.None,
                SecretVaultCapabilities.None,
                "windows-dpapi",
                "windows_dpapi_wrong_platform",
                "The Windows DPAPI vault is available only on Windows.");
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

        if (!Availability.CanPersist)
        {
            return ValueTask.FromResult(SecretVaultFailures.Unavailable<SecretMetadata>());
        }

        var plaintext = SecretVaultBuffers.Copy(material);
        return new ValueTask<SecretVaultResult<SecretMetadata>>(
            RunAsync(
                () => Create(request, plaintext),
                cancellationToken,
                plaintext));
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
        return denied is not null
            ? ValueTask.FromResult(denied)
            : RunAsync(() => Resolve(request), cancellationToken);
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

        if (!Availability.CanPersist)
        {
            return ValueTask.FromResult(SecretVaultFailures.Unavailable<SecretMetadata>());
        }

        var plaintext = SecretVaultBuffers.Copy(material);
        return new ValueTask<SecretVaultResult<SecretMetadata>>(
            RunAsync(
                () => Replace(request, plaintext),
                cancellationToken,
                plaintext));
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
        return denied is not null
            ? ValueTask.FromResult(denied)
            : RunAsync(() => Relabel(request), cancellationToken);
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
        return denied is not null
            ? ValueTask.FromResult(denied)
            : RunAsync(() => Delete(request), cancellationToken);
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
        return denied is not null
            ? ValueTask.FromResult(denied)
            : RunAsync(() => GetMetadata(request.Reference, request.Scope), cancellationToken);
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
        return denied is not null
            ? ValueTask.FromResult(denied)
            : RunAsync(() => ListMetadata(request), cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_optionalEntropy);
            _disposed = true;
        }
    }

    private SecretVaultResult<SecretMetadata> Create(CreateSecretRequest request, byte[] plaintext)
    {
        var path = GetPath(request.Reference);
        if (File.Exists(path))
        {
            return SecretVaultFailures.AlreadyExists<SecretMetadata>();
        }

        var now = DateTimeOffset.UtcNow;
        var metadata = new SecretMetadata(
            request.Reference,
            request.Label,
            request.Kind,
            request.Scope,
            SecretVaultPersistenceKind.OsProtectedPersistent,
            now,
            now);
        var protectedValue = ProtectedData.Protect(
            plaintext,
            _optionalEntropy,
            DataProtectionScope.CurrentUser);

        try
        {
            Write(path, new StoredSecret(FormatVersion, metadata, Convert.ToBase64String(protectedValue)), false);
            return SecretVaultResult<SecretMetadata>.Succeed(metadata);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedValue);
        }
    }

    private SecretVaultResult<SecretMaterial> Resolve(ResolveSecretRequest request)
    {
        var path = GetPath(request.Reference);
        if (!File.Exists(path))
        {
            return SecretVaultFailures.NotFound<SecretMaterial>();
        }

        var stored = Read(path, request.Reference);
        var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<SecretMaterial>(
            request.Scope,
            stored.Metadata.Scope);
        if (scopeMismatch is not null)
        {
            return scopeMismatch;
        }

        var protectedValue = Convert.FromBase64String(stored.ProtectedValue);
        byte[]? plaintext = null;

        try
        {
            plaintext = ProtectedData.Unprotect(
                protectedValue,
                _optionalEntropy,
                DataProtectionScope.CurrentUser);

            var metadata = stored.Metadata with { LastUsedAt = DateTimeOffset.UtcNow };
            Write(path, stored with { Metadata = metadata }, true);
            var material = SecretMaterial.TakeOwnership(plaintext);
            plaintext = null;
            return SecretVaultResult<SecretMaterial>.Succeed(material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedValue);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private SecretVaultResult<SecretMetadata> Replace(ReplaceSecretRequest request, byte[] plaintext)
    {
        var path = GetPath(request.Reference);
        if (!File.Exists(path))
        {
            return SecretVaultFailures.NotFound<SecretMetadata>();
        }

        var stored = Read(path, request.Reference);
        var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<SecretMetadata>(
            request.Scope,
            stored.Metadata.Scope);
        if (scopeMismatch is not null)
        {
            return scopeMismatch;
        }

        var protectedValue = ProtectedData.Protect(
            plaintext,
            _optionalEntropy,
            DataProtectionScope.CurrentUser);

        try
        {
            var metadata = stored.Metadata with { UpdatedAt = DateTimeOffset.UtcNow };
            Write(
                path,
                stored with
                {
                    Metadata = metadata,
                    ProtectedValue = Convert.ToBase64String(protectedValue),
                },
                true);
            return SecretVaultResult<SecretMetadata>.Succeed(metadata);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedValue);
        }
    }

    private SecretVaultResult<SecretMetadata> Relabel(RelabelSecretRequest request)
    {
        var path = GetPath(request.Reference);
        if (!File.Exists(path))
        {
            return SecretVaultFailures.NotFound<SecretMetadata>();
        }

        var stored = Read(path, request.Reference);
        var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<SecretMetadata>(
            request.Scope,
            stored.Metadata.Scope);
        if (scopeMismatch is not null)
        {
            return scopeMismatch;
        }

        var metadata = stored.Metadata with
        {
            Label = request.Label,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Write(path, stored with { Metadata = metadata }, true);
        return SecretVaultResult<SecretMetadata>.Succeed(metadata);
    }

    private SecretVaultResult<Unit> Delete(DeleteSecretRequest request)
    {
        var path = GetPath(request.Reference);
        if (!File.Exists(path))
        {
            return SecretVaultFailures.NotFound<Unit>();
        }

        var stored = Read(path, request.Reference);
        var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<Unit>(
            request.Scope,
            stored.Metadata.Scope);
        if (scopeMismatch is not null)
        {
            return scopeMismatch;
        }

        File.Delete(path);
        return SecretVaultResult<Unit>.Succeed(Unit.Value);
    }

    private SecretVaultResult<SecretMetadata> GetMetadata(
        SecretRef reference,
        SecretScope expectedScope)
    {
        var path = GetPath(reference);
        if (!File.Exists(path))
        {
            return SecretVaultFailures.NotFound<SecretMetadata>();
        }

        var metadata = Read(path, reference).Metadata;
        return SecretVaultAuthorization.MatchStoredScope<SecretMetadata>(
                expectedScope,
                metadata.Scope)
            ?? SecretVaultResult<SecretMetadata>.Succeed(metadata);
    }

    private SecretVaultResult<IReadOnlyList<SecretMetadata>> ListMetadata(
        ListSecretMetadataRequest request)
    {
        if (!Directory.Exists(_directory))
        {
            return SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([]);
        }

        var metadata = Directory
            .EnumerateFiles(_directory, $"*{FileExtension}", SearchOption.TopDirectoryOnly)
            .Select(path => Read(path, null).Metadata)
            .Where(item => request.Scope is null || item.Scope == request.Scope)
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Reference.Value, StringComparer.Ordinal)
            .ToArray();
        return SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed(metadata);
    }

    private StoredSecret Read(string path, SecretRef? expectedReference)
    {
        var bytes = File.ReadAllBytes(path);

        try
        {
            var stored = JsonSerializer.Deserialize(
                    bytes,
                    WindowsSecretJsonContext.Default.StoredSecret)
                ?? throw new JsonException("The secret entry was empty.");
            if (stored.FormatVersion != FormatVersion
                || expectedReference is { } expected && stored.Metadata.Reference != expected)
            {
                throw new JsonException("The secret entry has an unsupported shape.");
            }

            return stored;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void Write(string path, StoredSecret value, bool overwrite)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(_directory, $".{Path.GetRandomFileName()}.tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            WindowsSecretJsonContext.Default.StoredSecret);

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetPath(SecretRef reference)
    {
        var id = Encoding.UTF8.GetBytes(reference.Value);

        try
        {
            return Path.Combine(_directory, $"{Convert.ToHexString(SHA256.HashData(id))}{FileExtension}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(id);
        }
    }

    private ValueTask<SecretVaultResult<T>> RunAsync<T>(
        Func<SecretVaultResult<T>> operation,
        CancellationToken cancellationToken)
    {
        if (!Availability.CanPersist)
        {
            return ValueTask.FromResult(SecretVaultFailures.Unavailable<T>());
        }

        return new ValueTask<SecretVaultResult<T>>(Task.Run(
            () => Run(operation, cancellationToken),
            CancellationToken.None));
    }

    private Task<SecretVaultResult<T>> RunAsync<T>(
        Func<SecretVaultResult<T>> operation,
        CancellationToken cancellationToken,
        byte[] plaintext) =>
        Task.Run(
            () =>
            {
                try
                {
                    return Run(operation, cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            },
            CancellationToken.None);

    private SecretVaultResult<T> Run<T>(
        Func<SecretVaultResult<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }
        }
        catch (OperationCanceledException)
        {
            return SecretVaultFailures.Cancelled<T>();
        }
        catch (UnauthorizedAccessException)
        {
            return SecretVaultResult<T>.Fail(SecretVaultError.Create(SecretVaultErrorCode.AccessDenied));
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or CryptographicException)
        {
            return SecretVaultFailures.CorruptEntry<T>();
        }
        catch (IOException)
        {
            return SecretVaultFailures.PlatformFailure<T>(true);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return SecretVaultFailures.PlatformFailure<T>();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record StoredSecret(
        int FormatVersion,
        SecretMetadata Metadata,
        string ProtectedValue);

    [JsonSerializable(typeof(StoredSecret))]
    private sealed partial class WindowsSecretJsonContext : JsonSerializerContext;
}
