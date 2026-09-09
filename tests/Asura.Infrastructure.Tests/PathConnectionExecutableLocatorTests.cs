namespace Asura.Infrastructure.Tests;

public sealed class PathConnectionExecutableLocatorTests : IDisposable
{
    private readonly DirectoryInfo _temporaryDirectory =
        Directory.CreateTempSubdirectory("asura-executable-locator-");

    [Fact]
    public void Find_prefers_the_inherited_path()
    {
        var inherited = Directory.CreateDirectory(Path.Combine(_temporaryDirectory.FullName, "path"));
        var supplemental = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory.FullName, "supplemental"));
        var inheritedExecutable = CreateExecutable(inherited.FullName, "docker");
        _ = CreateExecutable(supplemental.FullName, "docker");
        var locator = new PathConnectionExecutableLocator(
            inherited.FullName,
            [supplemental.FullName]);

        var result = locator.Find("docker");

        Assert.Equal(inheritedExecutable, result);
    }

    [Fact]
    public void Find_uses_a_supplemental_directory_when_the_gui_path_is_minimal()
    {
        var supplemental = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory.FullName, "supplemental"));
        var executable = CreateExecutable(supplemental.FullName, "docker");
        var locator = new PathConnectionExecutableLocator(
            "/usr/bin:/bin:/usr/sbin:/sbin",
            [supplemental.FullName]);

        var result = locator.Find("docker");

        Assert.Equal(executable, result);
    }

    [Fact]
    public void Find_rejects_a_non_executable_supplemental_file()
    {
        var supplemental = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory.FullName, "supplemental"));
        var candidate = Path.Combine(supplemental.FullName, "docker");
        File.WriteAllText(candidate, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var locator = new PathConnectionExecutableLocator(null, [supplemental.FullName]);

        Assert.Null(locator.Find("docker"));
    }

    public void Dispose() => _temporaryDirectory.Delete(recursive: true);

    private static string CreateExecutable(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
