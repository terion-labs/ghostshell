namespace Asura.Infrastructure.Tests;

public sealed class BundledConnectionEngineExecutableLocatorTests : IDisposable
{
    private readonly DirectoryInfo _temporaryDirectory =
        Directory.CreateTempSubdirectory("asura-bundled-engine-locator-");

    [Fact]
    public void Find_prefers_a_bundled_connection_engine_over_path()
    {
        var bundle = Directory.CreateDirectory(Path.Combine(_temporaryDirectory.FullName, "bundle"));
        var path = Directory.CreateDirectory(Path.Combine(_temporaryDirectory.FullName, "path"));
        var bundled = CreateExecutable(bundle.FullName, "asura-openvpn-engine");
        _ = CreateExecutable(path.FullName, "asura-openvpn-engine");
        var locator = new BundledConnectionEngineExecutableLocator(
            new PathConnectionExecutableLocator(path.FullName, []),
            bundle.FullName,
            useBundle: true);

        Assert.Equal(bundled, locator.Find("asura-openvpn-engine"));
    }

    [Theory]
    [InlineData("asura-openvpn-engine")]
    [InlineData("openconnect")]
    [InlineData("tailscale")]
    [InlineData("tailscaled")]
    public void Find_does_not_fall_back_to_path_for_a_missing_product_engine(string name)
    {
        var bundle = Directory.CreateDirectory(Path.Combine(_temporaryDirectory.FullName, "bundle"));
        var path = Directory.CreateDirectory(Path.Combine(_temporaryDirectory.FullName, "path"));
        _ = CreateExecutable(path.FullName, name);
        var locator = new BundledConnectionEngineExecutableLocator(
            new PathConnectionExecutableLocator(path.FullName, []),
            bundle.FullName,
            useBundle: true);

        Assert.Null(locator.Find(name));
    }

    [Fact]
    public void Find_delegates_unrelated_executables_to_path_discovery()
    {
        var bundle = Directory.CreateDirectory(Path.Combine(_temporaryDirectory.FullName, "bundle"));
        var path = Directory.CreateDirectory(Path.Combine(_temporaryDirectory.FullName, "path"));
        var container = CreateExecutable(path.FullName, "container");
        var locator = new BundledConnectionEngineExecutableLocator(
            new PathConnectionExecutableLocator(path.FullName, []),
            bundle.FullName,
            useBundle: true);

        Assert.Equal(container, locator.Find("container"));
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
