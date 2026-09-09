using Asura.Application;

namespace Asura.Infrastructure;

internal static class SecretVaultFailures
{
    public static SecretVaultResult<T> Cancelled<T>() =>
        SecretVaultResult<T>.Fail(SecretVaultError.Create(SecretVaultErrorCode.Cancelled));

    public static SecretVaultResult<T> Unavailable<T>() =>
        SecretVaultResult<T>.Fail(SecretVaultError.Create(SecretVaultErrorCode.Unavailable));

    public static SecretVaultResult<T> NotFound<T>() =>
        SecretVaultResult<T>.Fail(SecretVaultError.Create(SecretVaultErrorCode.NotFound));

    public static SecretVaultResult<T> AlreadyExists<T>() =>
        SecretVaultResult<T>.Fail(SecretVaultError.Create(SecretVaultErrorCode.AlreadyExists));

    public static SecretVaultResult<T> AccessDenied<T>() =>
        SecretVaultResult<T>.Fail(SecretVaultError.Create(SecretVaultErrorCode.AccessDenied));

    public static SecretVaultResult<T> PlatformFailure<T>(bool retryable = false) =>
        SecretVaultResult<T>.Fail(SecretVaultError.Create(SecretVaultErrorCode.PlatformFailure, retryable));

    public static SecretVaultResult<T> AuditPersistenceFailure<T>() =>
        SecretVaultResult<T>.Fail(
            SecretVaultError.Create(SecretVaultErrorCode.AuditPersistenceFailure));

    public static SecretVaultResult<T> CorruptEntry<T>() =>
        SecretVaultResult<T>.Fail(SecretVaultError.Create(SecretVaultErrorCode.CorruptEntry));
}
