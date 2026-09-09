namespace Asura.Infrastructure;

public sealed record ConnectionRuntimeOptions(
    ConnectionHostPlatform Platform,
    string DefaultShell,
    string UserProfileDirectory)
{
    public static ConnectionRuntimeOptions Detect()
    {
        var userProfileDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Windows,
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                userProfileDirectory);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new ConnectionRuntimeOptions(
                ConnectionHostPlatform.MacOs,
                Environment.GetEnvironmentVariable("SHELL") ?? "/bin/zsh",
                userProfileDirectory);
        }

        if (OperatingSystem.IsLinux())
        {
            return new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Linux,
                Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh",
                userProfileDirectory);
        }

        return new ConnectionRuntimeOptions(
            ConnectionHostPlatform.Other,
            string.Empty,
            userProfileDirectory);
    }
}
