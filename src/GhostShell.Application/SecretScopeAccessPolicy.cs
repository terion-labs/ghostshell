namespace GhostShell.Application;

/// <summary>
/// Enforces that a secret purpose names the same resource as the secret scope.
/// Management purposes remain scoped and cannot reveal secret material.
/// </summary>
public sealed class SecretScopeAccessPolicy : ISecretAccessPolicy
{
    public static SecretScopeAccessPolicy Default { get; } = new();

    public bool IsAllowed(
        SecretVaultOperation operation,
        SecretScope? scope,
        SecretUsePurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        if (scope is null)
        {
            return operation == SecretVaultOperation.ListMetadata
                && purpose.Kind is SecretUseKind.UserManagement or SecretUseKind.PlatformMaintenance
                && string.Equals(
                    purpose.TargetId,
                    SecretUsePurpose.AllSecretsTargetId,
                    StringComparison.Ordinal);
        }

        var expectedTarget = scope.Kind == SecretScopeKind.Global
            ? SecretUsePurpose.GlobalTargetId
            : scope.OwnerId!;
        if (!string.Equals(purpose.TargetId, expectedTarget, StringComparison.Ordinal))
        {
            return false;
        }

        if (purpose.Kind == SecretUseKind.UserManagement)
        {
            return operation != SecretVaultOperation.Resolve;
        }

        if (purpose.Kind == SecretUseKind.PlatformMaintenance)
        {
            return true;
        }

        return scope.Kind == ScopeFor(purpose.Kind);
    }

    private static SecretScopeKind? ScopeFor(SecretUseKind purpose) => purpose switch
    {
        SecretUseKind.ConnectionAuthentication => SecretScopeKind.Connection,
        SecretUseKind.ConnectionEnvironment => SecretScopeKind.Connection,
        SecretUseKind.AiProviderAuthentication => SecretScopeKind.AiProvider,
        SecretUseKind.McpServerEnvironment => SecretScopeKind.McpServer,
        SecretUseKind.McpServerHttpHeader => SecretScopeKind.McpServer,
        SecretUseKind.BrowserProfileAuthentication => SecretScopeKind.BrowserProfile,
        SecretUseKind.FileProviderAuthentication => SecretScopeKind.FileProvider,
        SecretUseKind.DatabaseConnectionAuthentication => SecretScopeKind.DatabaseConnection,
        SecretUseKind.NetworkConnectionAuthentication => SecretScopeKind.NetworkConnection,
        _ => null,
    };
}
