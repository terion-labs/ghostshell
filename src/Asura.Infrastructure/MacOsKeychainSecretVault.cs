using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Stores Generic Password items through Security.framework. All SecItem calls run on a worker thread.
/// </summary>
public sealed class MacOsKeychainSecretVault : ISecretVault
{
    private const int Success = 0;
    private const int DuplicateItem = -25299;
    private const int ItemNotFound = -25300;
    private const int AuthFailed = -25293;
    private const int InteractionNotAllowed = -25308;
    private const int UserCancelled = -128;

    private readonly object _gate = new();
    private readonly string _serviceName;
    private readonly ISecretAccessPolicy _accessPolicy;
    private bool _disposed;

    public MacOsKeychainSecretVault(
        string serviceName,
        ISecretAccessPolicy? accessPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        _serviceName = serviceName;
        _accessPolicy = accessPolicy ?? SecretScopeAccessPolicy.Default;
        Availability = OperatingSystem.IsMacOS()
            ? new SecretVaultAvailability(
                SecretVaultAvailabilityState.Available,
                SecretVaultPersistenceKind.OsProtectedPersistent,
                SecretVaultCapabilities.All,
                "macos-keychain",
                "macos_keychain_services",
                "Credentials are stored in the current user's macOS Keychain.")
            : new SecretVaultAvailability(
                SecretVaultAvailabilityState.Unavailable,
                SecretVaultPersistenceKind.None,
                SecretVaultCapabilities.None,
                "macos-keychain",
                "macos_keychain_wrong_platform",
                "Keychain Services is available only on macOS.");
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
            _disposed = true;
        }
    }

    private SecretVaultResult<SecretMetadata> Create(CreateSecretRequest request, byte[] plaintext)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new SecretMetadata(
            request.Reference,
            request.Label,
            request.Kind,
            request.Scope,
            SecretVaultPersistenceKind.OsProtectedPersistent,
            now,
            now);

        using var item = CreateIdentityQuery(request.Reference);
        item.SetString(MacConstants.SecAttrLabel, metadata.Label);
        item.SetString(
            MacConstants.SecAttrComment,
            JsonSerializer.Serialize(metadata, InfrastructureJsonContext.Default.SecretMetadata));
        item.SetData(MacConstants.SecValueData, plaintext);

        var status = MacNative.SecItemAdd(item.Handle, out var result);
        ReleaseIfNeeded(result);
        return status == Success
            ? SecretVaultResult<SecretMetadata>.Succeed(metadata)
            : StatusFailure<SecretMetadata>(status);
    }

    private SecretVaultResult<SecretMaterial> Resolve(ResolveSecretRequest request)
    {
        var metadataResult = GetMetadata(request.Reference, request.Scope);
        if (metadataResult is not SecretVaultResult<SecretMetadata>.Success metadataSuccess)
        {
            return metadataResult switch
            {
                SecretVaultResult<SecretMetadata>.Failure metadataFailure =>
                    SecretVaultResult<SecretMaterial>.Fail(metadataFailure.Error),
                _ => SecretVaultFailures.PlatformFailure<SecretMaterial>(),
            };
        }

        using var query = CreateIdentityQuery(request.Reference);
        query.SetConstant(MacConstants.SecReturnData, MacConstants.BooleanTrue);
        query.SetConstant(MacConstants.SecMatchLimit, MacConstants.SecMatchLimitOne);

        var status = MacNative.SecItemCopyMatching(query.Handle, out var result);
        if (status != Success)
        {
            ReleaseIfNeeded(result);
            return StatusFailure<SecretMaterial>(status);
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = MacNative.CopyData(result);
            var metadata = metadataSuccess.Value with { LastUsedAt = DateTimeOffset.UtcNow };
            var update = UpdateMetadata(request.Reference, metadata, null);
            if (update is SecretVaultResult<SecretMetadata>.Failure updateFailure)
            {
                return SecretVaultResult<SecretMaterial>.Fail(updateFailure.Error);
            }

            var material = SecretMaterial.TakeOwnership(plaintext);
            plaintext = null;
            return SecretVaultResult<SecretMaterial>.Succeed(material);
        }
        finally
        {
            ReleaseIfNeeded(result);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private SecretVaultResult<SecretMetadata> Replace(ReplaceSecretRequest request, byte[] plaintext)
    {
        var existing = GetMetadata(request.Reference, request.Scope);
        if (existing is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            return failure;
        }

        var metadata = ((SecretVaultResult<SecretMetadata>.Success)existing).Value with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return UpdateMetadata(request.Reference, metadata, plaintext);
    }

    private SecretVaultResult<SecretMetadata> Relabel(RelabelSecretRequest request)
    {
        var existing = GetMetadata(request.Reference, request.Scope);
        if (existing is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            return failure;
        }

        var metadata = ((SecretVaultResult<SecretMetadata>.Success)existing).Value with
        {
            Label = request.Label,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return UpdateMetadata(request.Reference, metadata, null, request.Label);
    }

    private SecretVaultResult<Unit> Delete(DeleteSecretRequest request)
    {
        var metadata = GetMetadata(request.Reference, request.Scope);
        if (metadata is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            return SecretVaultResult<Unit>.Fail(failure.Error);
        }

        using var query = CreateIdentityQuery(request.Reference);
        var status = MacNative.SecItemDelete(query.Handle);
        return status == Success
            ? SecretVaultResult<Unit>.Succeed(Unit.Value)
            : StatusFailure<Unit>(status);
    }

    private SecretVaultResult<SecretMetadata> GetMetadata(
        SecretRef reference,
        SecretScope expectedScope)
    {
        using var query = CreateIdentityQuery(reference);
        query.SetConstant(MacConstants.SecReturnAttributes, MacConstants.BooleanTrue);
        query.SetConstant(MacConstants.SecMatchLimit, MacConstants.SecMatchLimitOne);

        var status = MacNative.SecItemCopyMatching(query.Handle, out var result);
        if (status != Success)
        {
            ReleaseIfNeeded(result);
            return StatusFailure<SecretMetadata>(status);
        }

        try
        {
            var metadata = ReadMetadata(result);
            if (metadata.Reference != reference)
            {
                return SecretVaultFailures.CorruptEntry<SecretMetadata>();
            }

            return SecretVaultAuthorization.MatchStoredScope<SecretMetadata>(
                    expectedScope,
                    metadata.Scope)
                ?? SecretVaultResult<SecretMetadata>.Succeed(metadata);
        }
        finally
        {
            ReleaseIfNeeded(result);
        }
    }

    private SecretVaultResult<IReadOnlyList<SecretMetadata>> ListMetadata(
        ListSecretMetadataRequest request)
    {
        using var query = new MacDictionary();
        query.SetConstant(MacConstants.SecClass, MacConstants.SecClassGenericPassword);
        query.SetString(MacConstants.SecAttrService, _serviceName);
        query.SetConstant(MacConstants.SecReturnAttributes, MacConstants.BooleanTrue);
        query.SetConstant(MacConstants.SecMatchLimit, MacConstants.SecMatchLimitAll);

        var status = MacNative.SecItemCopyMatching(query.Handle, out var result);
        if (status == ItemNotFound)
        {
            ReleaseIfNeeded(result);
            return SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([]);
        }

        if (status != Success)
        {
            ReleaseIfNeeded(result);
            return StatusFailure<IReadOnlyList<SecretMetadata>>(status);
        }

        try
        {
            var count = MacNative.CFArrayGetCount(result);
            var metadata = new List<SecretMetadata>(checked((int)count));
            for (nint index = 0; index < count; index++)
            {
                var item = MacNative.CFArrayGetValueAtIndex(result, index);
                var value = ReadMetadata(item);
                if (request.Scope is null || value.Scope == request.Scope)
                {
                    metadata.Add(value);
                }
            }

            return SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed(
                [.. metadata
                    .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Reference.Value, StringComparer.Ordinal)]);
        }
        finally
        {
            ReleaseIfNeeded(result);
        }
    }

    private SecretVaultResult<SecretMetadata> UpdateMetadata(
        SecretRef reference,
        SecretMetadata metadata,
        byte[]? plaintext,
        string? newLabel = null)
    {
        using var query = CreateIdentityQuery(reference);
        using var attributes = new MacDictionary();
        attributes.SetString(
            MacConstants.SecAttrComment,
            JsonSerializer.Serialize(metadata, InfrastructureJsonContext.Default.SecretMetadata));
        if (newLabel is not null)
        {
            attributes.SetString(MacConstants.SecAttrLabel, newLabel);
        }

        if (plaintext is not null)
        {
            attributes.SetData(MacConstants.SecValueData, plaintext);
        }

        var status = MacNative.SecItemUpdate(query.Handle, attributes.Handle);
        return status == Success
            ? SecretVaultResult<SecretMetadata>.Succeed(metadata)
            : StatusFailure<SecretMetadata>(status);
    }

    private SecretMetadata ReadMetadata(nint attributes)
    {
        var comment = MacNative.CFDictionaryGetValue(attributes, MacConstants.SecAttrComment);
        if (comment == nint.Zero)
        {
            throw new JsonException("Keychain metadata was missing.");
        }

        return JsonSerializer.Deserialize(
                MacNative.CopyString(comment),
                InfrastructureJsonContext.Default.SecretMetadata)
            ?? throw new JsonException("Keychain metadata was empty.");
    }

    private MacDictionary CreateIdentityQuery(SecretRef reference)
    {
        var query = new MacDictionary();
        query.SetConstant(MacConstants.SecClass, MacConstants.SecClassGenericPassword);
        query.SetString(MacConstants.SecAttrService, _serviceName);
        query.SetString(MacConstants.SecAttrAccount, reference.Value);
        return query;
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
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }
        }
        catch (OperationCanceledException)
        {
            return SecretVaultFailures.Cancelled<T>();
        }
        catch (JsonException)
        {
            return SecretVaultFailures.CorruptEntry<T>();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return SecretVaultFailures.PlatformFailure<T>();
        }
    }

    private static SecretVaultResult<T> StatusFailure<T>(int status) =>
        status switch
        {
            DuplicateItem => SecretVaultFailures.AlreadyExists<T>(),
            ItemNotFound => SecretVaultFailures.NotFound<T>(),
            UserCancelled => SecretVaultResult<T>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.UserCancelled)),
            AuthFailed => SecretVaultResult<T>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.AccessDenied)),
            InteractionNotAllowed => SecretVaultResult<T>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.AuthenticationRequired)),
            _ => SecretVaultFailures.PlatformFailure<T>(),
        };

    private static void ReleaseIfNeeded(nint value)
    {
        if (value != nint.Zero)
        {
            MacNative.CFRelease(value);
        }
    }

    private static class MacConstants
    {
        private static readonly nint Security = NativeLibrary.Load(MacNative.SecurityFramework);
        private static readonly nint CoreFoundation = NativeLibrary.Load(MacNative.CoreFoundationFramework);

        public static readonly nint SecClass = LoadPointer(Security, "kSecClass");
        public static readonly nint SecClassGenericPassword = LoadPointer(Security, "kSecClassGenericPassword");
        public static readonly nint SecAttrService = LoadPointer(Security, "kSecAttrService");
        public static readonly nint SecAttrAccount = LoadPointer(Security, "kSecAttrAccount");
        public static readonly nint SecAttrLabel = LoadPointer(Security, "kSecAttrLabel");
        public static readonly nint SecAttrComment = LoadPointer(Security, "kSecAttrComment");
        public static readonly nint SecValueData = LoadPointer(Security, "kSecValueData");
        public static readonly nint SecReturnData = LoadPointer(Security, "kSecReturnData");
        public static readonly nint SecReturnAttributes = LoadPointer(Security, "kSecReturnAttributes");
        public static readonly nint SecMatchLimit = LoadPointer(Security, "kSecMatchLimit");
        public static readonly nint SecMatchLimitOne = LoadPointer(Security, "kSecMatchLimitOne");
        public static readonly nint SecMatchLimitAll = LoadPointer(Security, "kSecMatchLimitAll");
        public static readonly nint BooleanTrue = LoadPointer(CoreFoundation, "kCFBooleanTrue");
        public static readonly nint TypeDictionaryKeyCallbacks =
            NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
        public static readonly nint TypeDictionaryValueCallbacks =
            NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryValueCallBacks");

        private static nint LoadPointer(nint library, string symbol) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));
    }

    private sealed class MacDictionary : IDisposable
    {
        public MacDictionary()
        {
            Handle = MacNative.CFDictionaryCreateMutable(
                nint.Zero,
                0,
                MacConstants.TypeDictionaryKeyCallbacks,
                MacConstants.TypeDictionaryValueCallbacks);
            if (Handle == nint.Zero)
            {
                throw new InvalidOperationException("CoreFoundation could not allocate a dictionary.");
            }
        }

        public nint Handle { get; }

        public void SetConstant(nint key, nint value) =>
            MacNative.CFDictionarySetValue(Handle, key, value);

        public void SetString(nint key, string value)
        {
            var nativeValue = MacNative.CFStringCreateWithCString(
                nint.Zero,
                value,
                MacNative.Utf8Encoding);
            if (nativeValue == nint.Zero)
            {
                throw new InvalidOperationException("CoreFoundation could not encode a string.");
            }

            try
            {
                MacNative.CFDictionarySetValue(Handle, key, nativeValue);
            }
            finally
            {
                MacNative.CFRelease(nativeValue);
            }
        }

        public void SetData(nint key, byte[] value)
        {
            var nativeValue = MacNative.CFDataCreate(nint.Zero, value, value.Length);
            if (nativeValue == nint.Zero)
            {
                throw new InvalidOperationException("CoreFoundation could not allocate secret data.");
            }

            try
            {
                MacNative.CFDictionarySetValue(Handle, key, nativeValue);
            }
            finally
            {
                MacNative.CFRelease(nativeValue);
            }
        }

        public void Dispose() => MacNative.CFRelease(Handle);
    }

    private static class MacNative
    {
        public const string SecurityFramework =
            "/System/Library/Frameworks/Security.framework/Security";
        public const string CoreFoundationFramework =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        public const uint Utf8Encoding = 0x08000100;

        [DllImport(SecurityFramework)]
        public static extern int SecItemAdd(nint attributes, out nint result);

        [DllImport(SecurityFramework)]
        public static extern int SecItemCopyMatching(nint query, out nint result);

        [DllImport(SecurityFramework)]
        public static extern int SecItemUpdate(nint query, nint attributesToUpdate);

        [DllImport(SecurityFramework)]
        public static extern int SecItemDelete(nint query);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFDictionaryCreateMutable(
            nint allocator,
            nint capacity,
            nint keyCallBacks,
            nint valueCallBacks);

        [DllImport(CoreFoundationFramework)]
        public static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFDictionaryGetValue(nint dictionary, nint key);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFStringCreateWithCString(
            nint allocator,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
            uint encoding);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFStringGetLength(nint value);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

        [DllImport(CoreFoundationFramework)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CFStringGetCString(
            nint value,
            nint buffer,
            nint bufferSize,
            uint encoding);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFDataGetLength(nint data);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFDataGetBytePtr(nint data);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFArrayGetCount(nint array);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFArrayGetValueAtIndex(nint array, nint index);

        [DllImport(CoreFoundationFramework)]
        public static extern void CFRelease(nint value);

        public static byte[] CopyData(nint data)
        {
            var length = checked((int)CFDataGetLength(data));
            if (length is <= 0 or > SecretMaterial.MaximumLength)
            {
                throw new InvalidOperationException("Keychain returned an invalid secret length.");
            }

            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
            return bytes;
        }

        public static string CopyString(nint value)
        {
            var length = CFStringGetLength(value);
            var maximumLength = CFStringGetMaximumSizeForEncoding(length, Utf8Encoding);
            var bufferSize = checked(maximumLength + 1);
            var buffer = Marshal.AllocHGlobal(bufferSize);

            try
            {
                if (!CFStringGetCString(value, buffer, bufferSize, Utf8Encoding))
                {
                    throw new InvalidOperationException("CoreFoundation could not decode metadata.");
                }

                return Marshal.PtrToStringUTF8(buffer)
                    ?? throw new InvalidOperationException("CoreFoundation returned empty metadata.");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
