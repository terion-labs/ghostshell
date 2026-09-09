using Asura.App;

namespace Asura.App.Tests;

public sealed class ProductDocumentLocatorTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"asura-product-documents-{Guid.NewGuid():N}");

    public ProductDocumentLocatorTests() => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Finds_notices_beside_the_cross_platform_executable()
    {
        var expected = WriteNotices(_temporaryDirectory);

        var actual = ProductDocumentLocator.FindThirdPartyNotices(_temporaryDirectory);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Finds_notices_in_the_macos_resources_directory()
    {
        var executableDirectory = Path.Combine(
            _temporaryDirectory,
            "Asura.app",
            "Contents",
            "MacOS");
        Directory.CreateDirectory(executableDirectory);
        var expected = WriteNotices(Path.Combine(
            _temporaryDirectory,
            "Asura.app",
            "Contents",
            "Resources",
            "Licenses"));

        var actual = ProductDocumentLocator.FindThirdPartyNotices(executableDirectory);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Missing_notices_fail_closed()
    {
        Assert.Null(ProductDocumentLocator.FindThirdPartyNotices(_temporaryDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private static string WriteNotices(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            ProductDocumentLocator.ThirdPartyNoticesFileName);
        File.WriteAllText(path, "notices");
        return path;
    }
}
