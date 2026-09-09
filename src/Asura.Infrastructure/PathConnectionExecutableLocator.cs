namespace Asura.Infrastructure;

/// <summary>
/// Resolves executables to absolute paths before a launch plan leaves Infrastructure.
/// </summary>
public sealed class PathConnectionExecutableLocator : IConnectionExecutableLocator
{
    private readonly string? _inheritedPath;
    private readonly IReadOnlyList<string> _supplementalDirectories;

    public PathConnectionExecutableLocator()
        : this(
            Environment.GetEnvironmentVariable("PATH"),
            DefaultSupplementalDirectories())
    {
    }

    internal PathConnectionExecutableLocator(
        string? inheritedPath,
        IReadOnlyList<string> supplementalDirectories)
    {
        ArgumentNullException.ThrowIfNull(supplementalDirectories);
        _inheritedPath = inheritedPath;
        _supplementalDirectories = supplementalDirectories;
    }

    public string? Find(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0'))
        {
            return null;
        }

        if (Path.IsPathRooted(executable) || ContainsDirectorySeparator(executable))
        {
            return ResolveCandidate(Path.GetFullPath(executable));
        }

        foreach (var directory in SearchDirectories())
        {
            foreach (var candidateName in CandidateNames(executable))
            {
                var candidate = ResolveCandidate(Path.Combine(directory, candidateName));
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private IEnumerable<string> SearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_inheritedPath))
        {
            foreach (var directory in _inheritedPath.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }

        foreach (var directory in _supplementalDirectories)
        {
            if (!string.IsNullOrWhiteSpace(directory) && seen.Add(directory))
            {
                yield return directory;
            }
        }
    }

    private static IReadOnlyList<string> DefaultSupplementalDirectories()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return [];
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Array.AsReadOnly(
        [
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/opt/local/bin",
            "/opt/podman/bin",
            "/Applications/Docker.app/Contents/Resources/bin",
            "/Applications/OrbStack.app/Contents/MacOS/xbin",
            "/Applications/Rancher Desktop.app/Contents/Resources/resources/darwin/bin",
            Path.Combine(userProfile, ".docker", "bin"),
            Path.Combine(userProfile, ".orbstack", "bin"),
        ]);
    }

    private static bool ContainsDirectorySeparator(string value) =>
        value.Contains(Path.DirectorySeparatorChar)
        || value.Contains(Path.AltDirectorySeparatorChar);

    private static IEnumerable<string> CandidateNames(string executable)
    {
        yield return executable;
        if (!OperatingSystem.IsWindows() || Path.HasExtension(executable))
        {
            yield break;
        }

        var extensions = Environment.GetEnvironmentVariable("PATHEXT")
            ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [".COM", ".EXE", ".BAT", ".CMD"];
        foreach (var extension in extensions)
        {
            yield return executable + extension.ToLowerInvariant();
            yield return executable + extension.ToUpperInvariant();
        }
    }

    private static string? ResolveCandidate(string candidate)
    {
        if (!File.Exists(candidate))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(candidate);
        if (OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        try
        {
            var mode = File.GetUnixFileMode(fullPath);
            const UnixFileMode executableBits =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (mode & executableBits) != UnixFileMode.None ? fullPath : null;
        }
        catch (PlatformNotSupportedException)
        {
            return fullPath;
        }
    }
}
