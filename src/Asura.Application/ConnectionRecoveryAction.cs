namespace Asura.Application;

public enum ConnectionRecoveryAction
{
    None,
    EditProfile,
    InstallRuntime,
    UnlockSecretVault,
    ProvideAuthentication,
    ReviewHostKey,
    GrantPermission,
    Retry,
    Reconnect,
    SelectContainer,
    SelectDistribution,
}
