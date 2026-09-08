using GhostShell.Application.ApplicationUpdates;
using Velopack;
using Velopack.Locators;

namespace GhostShell.Updates.Tests;

public sealed class VelopackApplyBoundaryTests
{
    [Fact]
    public void Apply_requests_restart_but_never_macOS_elevation()
    {
        var directory = Directory.CreateTempSubdirectory("ghostshell-updater-boundary-").FullName;
        try
        {
            var updater = Path.Combine(directory, "Update");
            File.WriteAllText(updater, "test placeholder; never executed");
            var process = new CapturingProcess();
            var locator = new CapturingLocator(directory, updater, process);
            var shutDown = false;
            var service = new VelopackApplicationUpdateService(
                new DistributionIdentity(DistributionSource.GitHubRelease, ApplicationUpdateStrategy.Velopack, "stable"),
                () => shutDown = true,
                locator);

            Assert.True(service.Snapshot.CanRestartToApply);
            service.RestartToApply();

            Assert.True(shutDown);
            Assert.Equal(updater, process.Executable);
            Assert.Contains("apply", process.Arguments, StringComparer.Ordinal);
            Assert.Equal(OperatingSystem.IsMacOS(), process.Arguments.Contains("--silent", StringComparer.Ordinal));
            Assert.DoesNotContain("--norestart", process.Arguments, StringComparer.Ordinal);
            Assert.Contains("--rootDir", process.Arguments, StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CapturingLocator(string directory, string updater, CapturingProcess process)
        : TestVelopackLocator(
            "GhostShell", "1.0.0", directory, directory, directory, updater,
            localPackage: new VelopackAsset { Version = SemanticVersion.Parse("2.0.0"), FileName = "test.nupkg" })
    {
        public override IProcessImpl Process => process;
    }

    private sealed class CapturingProcess : IProcessImpl
    {
        public string? Executable { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string GetCurrentProcessPath() => "/test/GhostShell";
        public uint GetCurrentProcessId() => 42;
        public void Exit(int exitCode) => throw new InvalidOperationException("The app owns shutdown.");
        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
        {
            Executable = exePath;
            Arguments = [.. args];
        }
    }
}
