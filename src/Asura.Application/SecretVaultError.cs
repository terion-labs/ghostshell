namespace Asura.Application;

public sealed record SecretVaultError(
    SecretVaultErrorCode Code,
    string StableCode,
    string Message,
    bool Retryable)
{
    public static SecretVaultError Create(SecretVaultErrorCode code, bool retryable = false) =>
        code switch
        {
            SecretVaultErrorCode.InvalidRequest =>
                new(code, "secret_invalid_request", "The secret request is invalid.", retryable),
            SecretVaultErrorCode.Unavailable =>
                new(code, "secret_vault_unavailable", "No usable secret vault is available.", retryable),
            SecretVaultErrorCode.NotFound =>
                new(code, "secret_not_found", "The requested secret was not found.", retryable),
            SecretVaultErrorCode.AlreadyExists =>
                new(code, "secret_already_exists", "A secret with that reference already exists.", retryable),
            SecretVaultErrorCode.AccessDenied =>
                new(code, "secret_access_denied", "Access to the secret is denied.", retryable),
            SecretVaultErrorCode.AuthenticationRequired =>
                new(code, "secret_authentication_required", "Operating-system authentication is required.", retryable),
            SecretVaultErrorCode.UserCancelled =>
                new(code, "secret_user_cancelled", "The secret operation was cancelled by the user.", retryable),
            SecretVaultErrorCode.CorruptEntry =>
                new(code, "secret_entry_corrupt", "The stored secret entry is invalid or corrupt.", retryable),
            SecretVaultErrorCode.PlatformFailure =>
                new(code, "secret_platform_failure", "The operating-system secret store failed.", retryable),
            SecretVaultErrorCode.AuditPersistenceFailure =>
                new(
                    code,
                    "secret_audit_persistence_failure",
                    "The secret operation produced a result, but its audit completion could not be persisted. Reconcile vault state before retrying.",
                    false),
            SecretVaultErrorCode.Cancelled =>
                new(code, "secret_operation_cancelled", "The secret operation was cancelled.", retryable),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        };
}
