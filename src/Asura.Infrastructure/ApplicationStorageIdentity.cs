namespace Asura.Infrastructure;

/// <summary>
/// Local builds must never open installed users' data or keychain items. Only
/// the packaging build opts into the stable production namespace; Release
/// configuration alone does not make a build a production application.
/// </summary>
public static class ApplicationStorageIdentity
{
#if ASURA_PRODUCTION
    public const string DirectoryName = "Asura";
    public const string PosixDirectoryName = "asura";
    public const string SecretServiceName = "sh.asura";
#else
    public const string DirectoryName = "Asura Development";
    public const string PosixDirectoryName = "asura-development";
    public const string SecretServiceName = "sh.asura.development";
#endif
}
