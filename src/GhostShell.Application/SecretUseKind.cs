namespace GhostShell.Application;

public enum SecretUseKind
{
    ConnectionAuthentication,
    ConnectionEnvironment,
    AiProviderAuthentication,
    McpServerEnvironment,
    McpServerHttpHeader,
    BrowserProfileAuthentication,
    FileProviderAuthentication,
    UserManagement,
    PlatformMaintenance,
    DatabaseConnectionAuthentication,
    NetworkConnectionAuthentication,
}
