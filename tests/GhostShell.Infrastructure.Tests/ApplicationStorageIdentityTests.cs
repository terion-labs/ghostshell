namespace GhostShell.Infrastructure.Tests;

public sealed class ApplicationStorageIdentityTests
{
    [Fact]
    public void Ordinary_builds_use_development_storage_even_in_Release_configuration()
    {
        Assert.Equal("GhostShell Development", ApplicationStorageIdentity.DirectoryName);
        Assert.Equal("ghostshell-development", ApplicationStorageIdentity.PosixDirectoryName);
        Assert.Equal("app.ghostshell.development", new SecretVaultFactoryOptions().ServiceName);
    }

    [Fact]
    public void Durable_browser_and_disposable_paths_use_the_same_build_namespace()
    {
        var data = GhostShellDataPaths.CreateDefault();
        var browser = BrowserProfileStoragePaths.CreateDefault();
        var artifacts = LocalArtifactPaths.CreateDefault();
        var directory = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? ApplicationStorageIdentity.DirectoryName
            : ApplicationStorageIdentity.PosixDirectoryName;

        Assert.Equal(directory, Path.GetFileName(data.DataDirectory));
        Assert.Equal(Path.Combine(data.DataDirectory, "ghostshell.db"), data.DatabasePath);
        Assert.Equal(Path.Combine(data.DataDirectory, "browser", "state"), browser.PersistentDirectory);
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), ApplicationStorageIdentity.DirectoryName, "browser-runtime"),
            browser.RuntimeDirectory);
        Assert.Equal(data.DataDirectory, artifacts.DurableDataDirectory);
        Assert.Contains(directory, artifacts.CacheDirectory, StringComparison.Ordinal);
        Assert.Contains(directory, artifacts.ApplicationLogDirectory, StringComparison.Ordinal);
    }
}
