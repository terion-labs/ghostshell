namespace GhostShell.Infrastructure;

/// <summary>
/// Local builds must never open installed users' data or keychain items. Only
/// the packaging build opts into the stable production namespace; Release
/// configuration alone does not make a build a production application.
/// </summary>
public static class ApplicationStorageIdentity
{
#if GHOSTSHELL_PRODUCTION
    public const string DirectoryName = "GhostShell";
    public const string PosixDirectoryName = "ghostshell";
    public const string SecretServiceName = "app.ghostshell";
#else
    public const string DirectoryName = "GhostShell Development";
    public const string PosixDirectoryName = "ghostshell-development";
    public const string SecretServiceName = "app.ghostshell.development";
#endif
}
