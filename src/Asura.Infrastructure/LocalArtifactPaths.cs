namespace Asura.Infrastructure;

/// <summary>
/// Names the only filesystem roots that the local artifact control may inspect
/// or mutate. The durable data directory is retained solely as a protected
/// boundary and is never enumerated.
/// </summary>
public sealed record LocalArtifactPaths
{
    public LocalArtifactPaths(
        string cacheDirectory,
        string applicationLogDirectory,
        string? activeApplicationLogPath = null,
        string? durableDataDirectory = null)
    {
        CacheDirectory = NormalizeDirectory(cacheDirectory, nameof(cacheDirectory));
        ApplicationLogDirectory = NormalizeDirectory(
            applicationLogDirectory,
            nameof(applicationLogDirectory));
        ActiveApplicationLogPath = activeApplicationLogPath is null
            ? null
            : NormalizeFile(activeApplicationLogPath, nameof(activeApplicationLogPath));
        DurableDataDirectory = durableDataDirectory is null
            ? null
            : NormalizeDirectory(durableDataDirectory, nameof(durableDataDirectory));

        RejectOverlappingCategoryRoots();
        RejectUnsafeDurableDataOverlap();
        RejectActiveLogOutsideLogRoot();
    }

    public string CacheDirectory { get; }

    public string ApplicationLogDirectory { get; }

    public string? ActiveApplicationLogPath { get; }

    public string? DurableDataDirectory { get; }

    /// <summary>
    /// Resolves platform-native disposable roots. Asura does not persist an
    /// active application log by default, so the protected active path is null.
    /// </summary>
    public static LocalArtifactPaths CreateDefault()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var durableDataDirectory = AsuraDataPaths.CreateDefault().DataDirectory;

        if (OperatingSystem.IsMacOS())
        {
            return new LocalArtifactPaths(
                Path.Combine(userProfile, "Library", "Caches", ApplicationStorageIdentity.DirectoryName),
                Path.Combine(userProfile, "Library", "Logs", ApplicationStorageIdentity.DirectoryName),
                durableDataDirectory: durableDataDirectory);
        }

        if (OperatingSystem.IsWindows())
        {
            var productDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationStorageIdentity.DirectoryName);
            return new LocalArtifactPaths(
                Path.Combine(productDirectory, "Cache"),
                Path.Combine(productDirectory, "Logs"),
                durableDataDirectory: durableDataDirectory);
        }

        var cacheHome = ResolveXdgHome(
            "XDG_CACHE_HOME",
            Path.Combine(userProfile, ".cache"));
        var stateHome = ResolveXdgHome(
            "XDG_STATE_HOME",
            Path.Combine(userProfile, ".local", "state"));
        return new LocalArtifactPaths(
            Path.Combine(cacheHome, ApplicationStorageIdentity.PosixDirectoryName),
            Path.Combine(stateHome, ApplicationStorageIdentity.PosixDirectoryName, "logs"),
            durableDataDirectory: durableDataDirectory);
    }

    internal string DirectoryFor(Asura.Application.LocalArtifactKind kind) => kind switch
    {
        Asura.Application.LocalArtifactKind.Cache => CacheDirectory,
        Asura.Application.LocalArtifactKind.InactiveApplicationLogs =>
            ApplicationLogDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal bool IsProtectedActiveLog(string path) =>
        ActiveApplicationLogPath is not null
        && string.Equals(path, ActiveApplicationLogPath, PathComparison);

    internal static bool IsContainedDescendant(string root, string path) =>
        path.StartsWith(
            root + Path.DirectorySeparatorChar,
            PathComparison);

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string ResolveXdgHome(string environmentName, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(environmentName);
        return !string.IsNullOrWhiteSpace(configured)
            && Path.IsPathFullyQualified(configured)
                ? configured
                : fallback;
    }

    private static string NormalizeDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(
                fullPath,
                Path.GetPathRoot(fullPath),
                PathComparison))
        {
            throw new ArgumentException(
                "A disposable artifact root cannot be a filesystem root.",
                parameterName);
        }

        return fullPath;
    }

    private static string NormalizeFile(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.GetFullPath(path);
    }

    private void RejectOverlappingCategoryRoots()
    {
        if (PathsOverlap(CacheDirectory, ApplicationLogDirectory))
        {
            throw new ArgumentException(
                "Cache and application-log roots must be disjoint.");
        }
    }

    private void RejectUnsafeDurableDataOverlap()
    {
        if (DurableDataDirectory is null)
        {
            return;
        }

        if (ArtifactRootCanReachDurableData(CacheDirectory)
            || ArtifactRootCanReachDurableData(ApplicationLogDirectory))
        {
            throw new ArgumentException(
                "A disposable artifact root cannot contain durable application data.");
        }
    }

    private void RejectActiveLogOutsideLogRoot()
    {
        if (ActiveApplicationLogPath is null
            || IsContainedDescendant(
                ApplicationLogDirectory,
                ActiveApplicationLogPath))
        {
            return;
        }

        throw new ArgumentException(
            "The active application log must be a file below the log root.",
            nameof(ActiveApplicationLogPath));
    }

    private bool ArtifactRootCanReachDurableData(string artifactRoot) =>
        string.Equals(artifactRoot, DurableDataDirectory, PathComparison)
        || IsContainedDescendant(artifactRoot, DurableDataDirectory!);

    private static bool PathsOverlap(string left, string right) =>
        string.Equals(left, right, PathComparison)
        || IsContainedDescendant(left, right)
        || IsContainedDescendant(right, left);
}
