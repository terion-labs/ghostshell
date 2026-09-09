namespace Asura.Infrastructure;

public sealed record AsuraDataPaths(string DataDirectory, string DatabasePath)
{
    public static AsuraDataPaths CreateDefault()
    {
        var dataDirectory = ResolveDataDirectory();
        return new(dataDirectory, Path.Combine(dataDirectory, "asura.db"));
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
