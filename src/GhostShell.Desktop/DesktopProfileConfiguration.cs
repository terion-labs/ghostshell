using GhostShell.Files;
using GhostShell.Infrastructure;

namespace GhostShell.Desktop;

/// <summary>One startup selection for all GhostShell-owned storage and vault namespaces.</summary>
public sealed class DesktopProfileConfiguration
{
    internal const string ThrowawaySwitch = "--throwaway-profile";
    internal const string ResumeThrowawaySwitch = "--resume-throwaway-profile";
    private const string SmokeServicePrefix = "app.ghostshell.development.smoke.";
    private DesktopProfileConfiguration(GhostShellDataPaths data, LocalArtifactPaths artifacts,
        BrowserProfileStoragePaths browser, string secretServiceName, bool isThrowaway)
    {
        Data = data;
        Artifacts = artifacts;
        Browser = browser;
        SecretServiceName = secretServiceName;
        IsThrowaway = isThrowaway;
    }

    public GhostShellDataPaths Data { get; }
    public LocalArtifactPaths Artifacts { get; }
    public BrowserProfileStoragePaths Browser { get; }
    public string SecretServiceName { get; }
    public bool IsThrowaway { get; }

    public static DesktopProfileConfiguration CreateDefault() => new(
        GhostShellDataPaths.CreateDefault(), LocalArtifactPaths.CreateDefault(),
        BrowserProfileStoragePaths.CreateDefault(), ApplicationStorageIdentity.SecretServiceName, false);

    internal static DesktopProfileConfiguration FromCommandLine(string[] arguments) =>
        FromCommandLine(arguments,
#if GHOSTSHELL_PRODUCTION
            productionBuild: true);
#else
            productionBuild: false);
#endif

    internal static DesktopProfileConfiguration FromCommandLine(string[] arguments, bool productionBuild)
    {
        var resume = arguments.Contains(ResumeThrowawaySwitch, StringComparer.Ordinal);
        var selectedSwitch = resume ? ResumeThrowawaySwitch : ThrowawaySwitch;
        var index = Array.IndexOf(arguments, selectedSwitch);
        if (index < 0)
        {
            if (arguments.Any(argument => argument.StartsWith(ThrowawaySwitch, StringComparison.Ordinal)
                || argument.StartsWith(ResumeThrowawaySwitch, StringComparison.Ordinal)))
            {
                throw new ArgumentException("Use --throwaway-profile followed by one absolute temporary directory path.");
            }
            return CreateDefault();
        }
        if (productionBuild || index + 1 >= arguments.Length
            || Array.LastIndexOf(arguments, selectedSwitch) != index
            || (resume && arguments.Contains(ThrowawaySwitch, StringComparer.Ordinal))
            || !Path.IsPathFullyQualified(arguments[index + 1]))
        {
            throw new ArgumentException("A throwaway profile requires a development build and one new private temporary directory.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(arguments[index + 1]));
        var temporaryRoot = ResolvePhysicalDirectory(Path.GetTempPath());
        if (!root.StartsWith(temporaryRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !Directory.Exists(root))
        {
            throw new ArgumentException("The throwaway profile must be an existing new directory beneath the system temporary directory.");
        }
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null)
            {
                throw new ArgumentException("A throwaway profile cannot contain linked path components. Use its physical path.");
            }
        }
        PrivateContentPathGuard.ValidatePrivateDirectory(root);
        var claimPath = Path.Combine(root, "throwaway-profile.txt");
        if (resume)
        {
            PrivateContentPathGuard.ValidatePrivateFile(claimPath);
            if (new FileInfo(claimPath).Length > 128)
            {
                throw new ArgumentException("The throwaway profile claim is invalid.");
            }
            var existingService = File.ReadAllText(claimPath).TrimEnd('\r', '\n');
            if (!existingService.StartsWith(SmokeServicePrefix, StringComparison.Ordinal)
                || existingService.Length != SmokeServicePrefix.Length + 32
                || !Guid.TryParseExact(existingService[SmokeServicePrefix.Length..], "N", out _))
            {
                throw new ArgumentException("The throwaway profile claim does not name a generated test namespace.");
            }
            return CreateThrowaway(root, existingService);
        }
        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new ArgumentException("The throwaway profile directory must be empty; existing profiles are never reused or migrated.");
        }

        var service = SmokeServicePrefix + Guid.NewGuid().ToString("N");
        var claimOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            claimOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        // Only the explicit development resume switch can reuse this non-secret identity.
        using (var claim = new StreamWriter(new FileStream(claimPath, claimOptions)))
        {
            claim.WriteLine(service);
        }
        return CreateThrowaway(root, service);
    }

    private static DesktopProfileConfiguration CreateThrowaway(string root, string service)
    {
        var dataDirectory = Path.Combine(root, "data");
        return new(new(dataDirectory, Path.Combine(dataDirectory, "ghostshell.db")),
            new(Path.Combine(root, "cache"), Path.Combine(root, "logs"), durableDataDirectory: dataDirectory),
            new(Path.Combine(dataDirectory, "browser", "state"), Path.Combine(root, "browser-runtime")), service, true);
    }

    private static string ResolvePhysicalDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = Path.GetPathRoot(fullPath)!;
        foreach (var part in fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
}
