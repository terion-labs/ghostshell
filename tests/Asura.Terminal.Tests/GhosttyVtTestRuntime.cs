using System.Runtime.InteropServices;
using Asura.Terminal.GhosttyVt;

namespace Asura.Terminal.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GhosttyVtTestCollection
{
    public const string Name = "libghostty-vt";
}

internal static class GhosttyVtTestRuntime
{
    private const string RuntimePathVariable = "ASURA_GHOSTTY_VT_PATH";
    private static readonly object Gate = new();
    private static bool _configured;

    internal static GhosttyVtRuntimeAvailability RequireStagedRuntime()
    {
        lock (Gate)
        {
            if (!_configured)
            {
                var libraryPath = ResolveStagedLibraryPath();
                Assert.True(
                    File.Exists(libraryPath),
                    $"The staged libghostty-vt test runtime is missing: {libraryPath}");
                Environment.SetEnvironmentVariable(RuntimePathVariable, libraryPath);
                _configured = true;
            }
        }

        var availability = GhosttyVtRuntimeProbe.Detect();
        Assert.True(availability.IsAvailable, availability.Detail);
        Assert.True(availability.SupportsKittyGraphics);
        return availability;
    }

    private static string ResolveStagedLibraryPath()
    {
        var repository = FindRepositoryRoot();
        var runtimeIdentifier = (OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, Architecture.Arm64) => "osx-arm64",
            (true, Architecture.X64) => "osx-x64",
            (false, Architecture.Arm64) when OperatingSystem.IsLinux() => "linux-arm64",
            (false, Architecture.X64) when OperatingSystem.IsLinux() => "linux-x64",
            (false, Architecture.X64) when OperatingSystem.IsWindows() => "win-x64",
            _ => throw new PlatformNotSupportedException(
                $"No staged libghostty-vt test runtime is defined for {RuntimeInformation.OSDescription} " +
                $"({RuntimeInformation.ProcessArchitecture})."),
        };
        var fileName = OperatingSystem.IsWindows()
            ? "ghostty-vt.dll"
            : OperatingSystem.IsMacOS()
                ? "libghostty-vt.dylib"
                : "libghostty-vt.so";

        return Path.Combine(repository, "native", "artifacts", runtimeIdentifier, fileName);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the Asura repository above {AppContext.BaseDirectory}.");
    }
}
