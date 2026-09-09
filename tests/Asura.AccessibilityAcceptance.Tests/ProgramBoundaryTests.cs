using System.Diagnostics;

namespace Asura.AccessibilityAcceptance.Tests;

public sealed class ProgramBoundaryTests
{
    [Fact]
    public void Package_exit_boundary_observes_a_parent_that_exits_during_tree_wait()
    {
        using var process = StartShortLivedProcess();
        var processTree = ProcessTreeTracker.Attach(process);
        var observations = EvidenceFixture.Valid().Checks.ToList();

        Program.ApplyPackageExitBoundary(process, processTree, observations);

        var packageExited = observations[^1].Assertions.Single(
            assertion => string.Equals(assertion.Id, "package-exited", StringComparison.Ordinal));
        Assert.True(process.HasExited);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(AcceptanceStatus.Pass, packageExited.Result);
    }

    private static Process StartShortLivedProcess()
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("ping -n 2 127.0.0.1 > nul");
        }
        else
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("sleep 0.25");
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException("The short-lived process did not start.");
    }
}
