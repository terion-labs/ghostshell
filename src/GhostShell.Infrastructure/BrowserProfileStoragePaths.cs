namespace GhostShell.Infrastructure;

/// <summary>
/// Separates the encrypted durable browser archive from CEF's owner-private
/// working tree. The working tree is deleted after it has been sealed.
/// </summary>
public sealed record BrowserProfileStoragePaths
{
    public BrowserProfileStoragePaths(
        string persistentDirectory,
        string runtimeDirectory)
    {
        PersistentDirectory = Normalize(persistentDirectory);
        RuntimeDirectory = Normalize(runtimeDirectory);
        if (PathsOverlap(PersistentDirectory, RuntimeDirectory))
        {
            throw new ArgumentException(
                "Browser archive and runtime directories must be disjoint.");
        }
    }

    public string PersistentDirectory { get; }

    public string RuntimeDirectory { get; }

    public static BrowserProfileStoragePaths CreateDefault()
    {
        var data = GhostShellDataPaths.CreateDefault().DataDirectory;
        return new BrowserProfileStoragePaths(
            Path.Combine(data, "browser", "state"),
            Path.Combine(Path.GetTempPath(), ApplicationStorageIdentity.DirectoryName, "browser-runtime"));
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(
                result,
                Path.GetPathRoot(result),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A browser storage directory cannot be a filesystem root.",
                nameof(path));
        }

        return result;
    }

    private static bool PathsOverlap(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(first, second, comparison)
            || first.StartsWith(second + Path.DirectorySeparatorChar, comparison)
            || second.StartsWith(first + Path.DirectorySeparatorChar, comparison);
    }
}
