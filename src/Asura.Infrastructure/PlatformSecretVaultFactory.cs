using Asura.Application;

namespace Asura.Infrastructure;

public static class PlatformSecretVaultFactory
{
    public static SecretVaultFactoryResult Create(SecretVaultFactoryOptions? options = null)
    {
        options ??= new SecretVaultFactoryOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceName);

        var platform = ResolvePlatform(options.Platform);
        ISecretVault vault = platform switch
        {
            SecretVaultPlatform.MacOS => CreateMacOs(options.ServiceName, options.AccessPolicy),
            SecretVaultPlatform.Windows => CreateWindows(options),
            SecretVaultPlatform.Linux => CreateLinux(options),
            _ => new UnavailableSecretVault(
                "platform_not_supported",
                "This operating system does not have a configured Asura secret-vault adapter.",
                accessPolicy: options.AccessPolicy),
        };

        if (options.AuditSink is not null)
        {
            vault = new AuditedSecretVault(vault, options.AuditSink);
        }

        var availability = vault.Availability;
        return new SecretVaultFactoryResult(
            vault,
            new SecretVaultFactoryDiagnostic(
                platform,
                availability.Adapter,
                availability.DiagnosticCode,
                availability.Message,
                availability));
    }

    private static ISecretVault CreateMacOs(
        string serviceName,
        ISecretAccessPolicy? accessPolicy)
    {
        try
        {
            return new MacOsKeychainSecretVault(serviceName, accessPolicy);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return new UnavailableSecretVault(
                "macos_keychain_unavailable",
                "macOS Keychain Services could not be loaded.",
                "macos-keychain",
                accessPolicy);
        }
    }

    private static ISecretVault CreateWindows(SecretVaultFactoryOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UnavailableSecretVault(
                "windows_dpapi_wrong_platform",
                "The Windows DPAPI vault is available only on Windows.",
                "windows-dpapi",
                options.AccessPolicy);
        }

        return new WindowsDpapiSecretVault(
            GetDataDirectory(options.DataDirectory, "vault"),
            options.ServiceName,
            options.AccessPolicy);
    }

    private static ISecretVault CreateLinux(SecretVaultFactoryOptions options)
    {
        var executable = LinuxSecretServiceSecretVault.FindSecretTool(options.LinuxSecretToolPath);
        if (executable is null)
        {
            return new UnavailableSecretVault(
                "linux_secret_service_tool_missing",
                "Secret Service support requires the secret-tool executable.",
                "linux-secret-service",
                options.AccessPolicy);
        }

        return new LinuxSecretServiceSecretVault(
            executable,
            options.ServiceName,
            GetDataDirectory(options.DataDirectory, "vault-metadata"),
            options.AccessPolicy);
    }

    private static string GetDataDirectory(string? configuredDirectory, string leaf)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory);
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            return Path.Combine(AppContext.BaseDirectory, "." + ApplicationStorageIdentity.PosixDirectoryName, leaf);
        }

        return Path.Combine(localData, ApplicationStorageIdentity.DirectoryName, leaf);
    }

    private static SecretVaultPlatform ResolvePlatform(SecretVaultPlatform requested)
    {
        if (requested != SecretVaultPlatform.Automatic)
        {
            return requested;
        }

        if (OperatingSystem.IsMacOS())
        {
            return SecretVaultPlatform.MacOS;
        }

        if (OperatingSystem.IsWindows())
        {
            return SecretVaultPlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return SecretVaultPlatform.Linux;
        }

        return SecretVaultPlatform.Unsupported;
    }
}
