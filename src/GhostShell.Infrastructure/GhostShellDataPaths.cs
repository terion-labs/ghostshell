namespace GhostShell.Infrastructure;

public sealed record GhostShellDataPaths(string DataDirectory, string DatabasePath)
{
    public static GhostShellDataPaths CreateDefault()
    {
        var dataDirectory = ResolveDataDirectory();
        return new(dataDirectory, Path.Combine(dataDirectory, "ghostshell.db"));
    }

    private static string ResolveDataDirectory()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ApplicationStorageIdentity.DirectoryName);
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationStorageIdentity.DirectoryName);
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, ApplicationStorageIdentity.PosixDirectoryName);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share",
            ApplicationStorageIdentity.PosixDirectoryName);
    }
}
