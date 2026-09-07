namespace GhostShell.Architecture.Tests;

public sealed class DevelopmentStorageBoundaryTests
{
    [Fact]
    public void Only_packaging_explicitly_selects_production_storage()
    {
        var props = Read("Directory.Build.props");
        Assert.Contains("'$(GhostShellProductionBuild)' == 'true'", props, StringComparison.Ordinal);
        Assert.Contains("$(DefineConstants);GHOSTSHELL_PRODUCTION", props, StringComparison.Ordinal);
        var packaging = Read("scripts/package-macos.sh");
        Assert.Equal(3, packaging.Split("-p:GhostShellProductionBuild=true", StringSplitOptions.None).Length - 1);
        var development = Read("scripts/run-macos-development.sh");
        Assert.DoesNotContain("GhostShellProductionBuild=true", development, StringComparison.Ordinal);
        Assert.Contains("CFBundleIdentifier -string app.ghostshell.development", development, StringComparison.Ordinal);
        Assert.Contains("CFBundleDisplayName -string \"GhostShell Development\"", development, StringComparison.Ordinal);
    }

    [Fact]
    public void Rehearsal_never_changes_the_users_default_keychain()
    {
        var rehearsal = Read("scripts/rehearse-macos-release.sh");
        Assert.DoesNotContain("security default-keychain", rehearsal, StringComparison.Ordinal);
        Assert.Contains("--keychain \"${signing_keychain}\"", rehearsal, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_error_UI_is_only_started_by_synchronous_Main()
    {
        var program = Read("src/GhostShell.Desktop/Program.cs");
        var preparation = program.IndexOf("private static async Task<StartupPreparation?> PrepareAsync()", StringComparison.Ordinal);
        Assert.True(preparation > 0);
        Assert.DoesNotContain("DesktopStartupFailurePresenter.TryShow", program[preparation..], StringComparison.Ordinal);
        var main = program[..preparation];
        Assert.Contains("var prepared = PrepareAsync().GetAwaiter().GetResult();", main, StringComparison.Ordinal);
        Assert.Contains("prepared is StartupPreparation.Failed failure", main, StringComparison.Ordinal);
        Assert.Contains("DesktopStartupFailurePresenter.TryShow", main, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
            }
        }
        throw new DirectoryNotFoundException("Unable to locate the GhostShell repository root.");
    }
}
